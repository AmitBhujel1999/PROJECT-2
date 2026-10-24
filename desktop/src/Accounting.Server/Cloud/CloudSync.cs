using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounting.Core.Common;
using Accounting.Core.Security;
using Microsoft.Extensions.Hosting;

namespace Accounting.Server.Cloud;

/// <summary>
/// The online copy for phones. While a company has it switched on, this keeps a copy of its screens'
/// data in Firebase: every page the phone may open (lists, documents, ledgers, reports) is fetched
/// from this server exactly as the web screens get it, compressed, and uploaded when it changed.
/// Phones join by pairing on the local network (<see cref="StartPairing"/>) and can only read the
/// pages their user's role allows (firebase/firestore.rules).
///
/// Firestore layout: books/{book} (owner, members) and books/{book}/views/{page} (gzip+base64 JSON in
/// one or more parts), books/{book}/pairing/{code} (a phone asking to join).
/// </summary>
public sealed class CloudSync(CompanyRegistry companies, Firebase firebase, Func<string> serverUrl) : BackgroundService
{
    /// <summary>Lets the sync read this server's pages as the company's administrator (loopback only).</summary>
    public static readonly string InternalKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FullRefresh = TimeSpan.FromHours(6);
    private const int PartChars = 900_000;

    private sealed record Pairing(string Slug, long UserId, string Me, string[] Perms, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<string, Pairing> _pairings = new();
    private readonly ConcurrentDictionary<string, FirebaseSession> _sessions = new();
    private readonly ConcurrentDictionary<string, bool> _forced = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly HttpClient _self = new() { Timeout = TimeSpan.FromSeconds(60) };

    public CloudConfig Config => firebase.Config;

    // ------------------------------------------------------------------ settings (CloudApi)

    public static Dictionary<string, string> State(Database db) =>
        db.Read(tx => tx.Rows("SELECT key, value FROM cloud_state").ToDictionary(r => r.Str("key"), r => r.Str("value")));

    private static void SetState(Database db, string key, string? value) => db.Write(tx =>
    {
        if (value is null)
            tx.Execute("DELETE FROM cloud_state WHERE key = @key", new { key });
        else
            tx.Execute("INSERT INTO cloud_state (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = @value", new { key, value });
        return 0;
    });

    public void SetEnabled(string slug, bool enabled)
    {
        var db = companies.Get(slug)!;
        SetState(db, "enabled", enabled ? "1" : "0");
        SetState(db, "last_error", null);
        if (enabled)
            _forced[slug] = true;
    }

    public void SyncSoon(string slug) => _forced[slug] = true;

    /// <summary>A one-time code (10 minutes) the phone of <paramref name="user"/> uses to join.</summary>
    public (string Code, string Url, DateTime ExpiresUtc) StartPairing(string slug, User user, string me, IEnumerable<string> perms)
    {
        var db = companies.Get(slug)!;
        var state = State(db);
        if (state.GetValueOrDefault("enabled") != "1" || !state.TryGetValue("book", out var book))
            throw new Core.BusinessError("The online copy is switched off. An administrator can switch it on under Settings > Phone anywhere.",
                "CLOUD_OFF");
        foreach (var old in _pairings.Where(p => p.Value.ExpiresUtc < DateTime.UtcNow).Select(p => p.Key).ToList())
            _pairings.TryRemove(old, out _);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expires = DateTime.UtcNow.AddMinutes(10);
        _pairings[code] = new Pairing(slug, user.Id, me, perms.ToArray(), expires);
        return (code, $"{firebase.Config.AppUrl}/link?b={book}&c={code}", expires);
    }

    public void RemoveDevice(string slug, string uid)
    {
        var db = companies.Get(slug)!;
        db.Write(tx => tx.Execute("DELETE FROM cloud_devices WHERE uid = @uid", new { uid }));
        _forced[slug] = true;
    }

    // ------------------------------------------------------------------ background loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        while (!ct.IsCancellationRequested)
        {
            foreach (var (slug, _) in companies.List())
            {
                try
                {
                    await TickAsync(slug, wait: false, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    SetState(companies.Get(slug)!, "last_error", e is CloudError or HttpRequestException ? e.Message : e.GetType().Name + ": " + e.Message);
                }
            }
            var waiting = _pairings.Values.Any(p => p.ExpiresUtc > DateTime.UtcNow) || !_forced.IsEmpty;
            await Task.Delay(waiting ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15), ct);
        }
    }

    /// <summary>One round for one company now (waits for a round already running): join waiting phones, upload what changed.</summary>
    public Task TickAsync(string slug, CancellationToken ct) => TickAsync(slug, wait: true, ct);

    private async Task TickAsync(string slug, bool wait, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(wait ? Timeout.Infinite : 0, ct))
            return;
        try
        {
            var db = companies.Get(slug)!;
            var state = State(db);
            if (state.GetValueOrDefault("enabled") != "1")
                return;
            var session = await SessionAsync(slug, db, state, ct);
            var book = state.GetValueOrDefault("book") ?? NewBook(db);

            var joined = _pairings.Any(p => p.Value.Slug == slug && p.Value.ExpiresUtc > DateTime.UtcNow)
                && await AcceptPairingsAsync(slug, db, book, session, ct);

            var version = Version(db);
            var lastSync = DateTime.TryParse(state.GetValueOrDefault("last_sync"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t : DateTime.MinValue;
            var forced = _forced.TryRemove(slug, out _);
            var changed = version != state.GetValueOrDefault("version");
            var due = forced || joined || DateTime.UtcNow - lastSync > FullRefresh || (changed && DateTime.UtcNow - lastSync > MinGap);
            if (!due)
                return;
            await SyncAsync(slug, db, book, session, ct);
            SetState(db, "version", version);
            SetState(db, "last_sync", DateTime.UtcNow.ToString("O"));
            SetState(db, "last_error", null);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string NewBook(Database db)
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var book = new string(RandomNumberGenerator.GetBytes(20).Select(b => alphabet[b % alphabet.Length]).ToArray());
        SetState(db, "book", book);
        return book;
    }

    /// <summary>Changes whenever anything in the books changes (every change is in the audit log), or the day changes.</summary>
    private static string Version(Database db) => db.Read(tx =>
        $"{tx.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM audit_log")}|{tx.Today:yyyy-MM-dd}|" +
        $"{tx.Scalar<string>("SELECT updated_at FROM business_settings WHERE id = 1")}|" +
        $"{tx.Scalar<string>("SELECT group_concat(uid) FROM (SELECT uid FROM cloud_devices ORDER BY uid)") ?? ""}");

    private async Task<FirebaseSession> SessionAsync(string slug, Database db, Dictionary<string, string> state, CancellationToken ct)
    {
        if (_sessions.TryGetValue(slug, out var s) && s.ExpiresUtc > DateTime.UtcNow)
            return s;
        s = state.TryGetValue("refresh", out var refresh)
            ? await firebase.RefreshAsync(refresh, ct)
            : await firebase.SignUpAnonymousAsync(ct);
        SetState(db, "uid", s.Uid);
        SetState(db, "refresh", s.RefreshToken);
        _sessions[slug] = s;
        return s;
    }

    // ------------------------------------------------------------------ pairing

    private async Task<bool> AcceptPairingsAsync(string slug, Database db, string book, FirebaseSession session, CancellationToken ct)
    {
        // The book document must exist (with us as owner) before phones can ask to join.
        await firebase.CommitAsync(session.IdToken, [BookDoc(db, book, session.Uid)], ct);
        var requests = await firebase.ListAsync(session.IdToken, $"books/{book}/pairing", ct);
        var joined = false;
        var writes = new List<JsonObject>();
        foreach (var doc in requests)
        {
            var name = doc["name"]!.GetValue<string>();
            var code = name[(name.LastIndexOf('/') + 1)..];
            var uid = Firebase.FieldString(doc, "uid");
            var device = Firebase.FieldString(doc, "device") ?? "";
            writes.Add(firebase.Delete($"books/{book}/pairing/{code}"));
            if (uid is null || !_pairings.TryRemove(code, out var pairing) || pairing.Slug != slug || pairing.ExpiresUtc < DateTime.UtcNow)
                continue;
            db.Write(tx => tx.Execute(
                """
                INSERT INTO cloud_devices (uid, user_id, name, me, perms) VALUES (@uid, @userId, @name, @me, @perms)
                ON CONFLICT (uid) DO UPDATE SET user_id = @userId, name = @name, me = @me, perms = @perms
                """,
                new { uid, userId = pairing.UserId, name = device.Length > 60 ? device[..60] : device, me = pairing.Me, perms = string.Join(",", pairing.Perms) }));
            joined = true;
        }
        if (joined)
            writes.Add(BookDoc(db, book, session.Uid));
        if (writes.Count > 0)
            await firebase.CommitAsync(session.IdToken, writes, ct);
        return joined;
    }

    /// <summary>books/{book}: owner, company name, and the phones allowed in (their user, role permissions).</summary>
    private JsonObject BookDoc(Database db, string book, string ownerUid)
    {
        var (name, devices) = db.Read(tx => (
            BusinessSettings.Get(tx).BusinessName,
            tx.Rows("SELECT * FROM cloud_devices").Select(r => (Uid: r.Str("uid"), Name: r.Str("name"), Me: r.Str("me"), Perms: r.Str("perms"))).ToList()));
        var members = new JsonObject();
        foreach (var d in devices)
            members[d.Uid] = Firebase.Map(new JsonObject
            {
                ["device"] = Firebase.Str(d.Name),
                ["me"] = Firebase.Str(d.Me),
                ["perms"] = Firebase.Array(d.Perms.Split(',', StringSplitOptions.RemoveEmptyEntries)),
            });
        return firebase.Update($"books/{book}", new JsonObject
        {
            ["owner"] = Firebase.Str(ownerUid),
            ["name"] = Firebase.Str(name),
            ["members"] = Firebase.Map(members),
            ["updated"] = Firebase.Time(DateTime.UtcNow),
        });
    }

    // ------------------------------------------------------------------ uploading pages

    /// <summary>A page the phone can open: its address (as the web screens ask for it), the query used
    /// to make the copy, and the permission needed to see it.</summary>
    public sealed record View(string Path, string Query, string Perm);

    /// <summary>Every page of the online copy for this company, as of today.</summary>
    public static List<View> Views(Database db)
    {
        var today = db.Today();
        var (y, m, _) = NepaliDate.FromAd(today);
        var fyStart = NepaliDate.ToAd(m >= 4 ? y : y - 1, 4, 1);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        string d(DateOnly x) => x.ToString("yyyy-MM-dd");
        var period = $"start_date={d(fyStart)}&end_date={d(today)}";
        const string all = "page_size=all";

        var views = new List<View>
        {
            new("settings/", "", ""),
            new("dashboard/", $"start_date={d(today.AddDays(-29))}&end_date={d(today)}", "dashboard.view"),
            new("products/", all, "products.view"),
            new("customers/", all, "parties.view"),
            new("vendors/", all, "parties.view"),
            new("sales/", all, "sales.view"),
            new("purchases/", all, "purchases.view"),
            new("customer-receipts/", all, "receipts.view"),
            new("vendor-payments/", all, "payments.view"),
            new("expenses/", all, "expenses.view"),
            new("expense-categories/", all, "expenses.view"),
            new("cash-bank/accounts/", all, "cashbank.view"),
            new("cash-bank/contras/", all, "cashbank.view"),
            new("inventory/adjustments/", all, "inventory.view"),
            new("journal/vouchers/", all, "journal.view"),
            new("journal/ledgers/", all, "journal.view"),
            new("journal/ledgers/groups/", "", "journal.view"),
            new("reports/stock/", all, "inventory.view"),
            new("reports/sales/", all, "reports.view"),
            new("reports/purchases/", all, "reports.view"),
            new("reports/expenses/", all, "reports.view"),
            new("reports/receivables/", $"as_of={d(today)}&{all}", "reports.view"),
            new("reports/payables/", $"as_of={d(today)}&{all}", "reports.view"),
            new("reports/receivables-aging/", $"as_of={d(today)}&{all}", "reports.view"),
            new("reports/payables-aging/", $"as_of={d(today)}&{all}", "reports.view"),
            new("reports/day-book/", $"start_date={d(today)}&end_date={d(today)}", "reports.view"),
            new("reports/profit-loss/", period, "reports.view"),
            new("reports/trial-balance/", period, "reports.view"),
            new("reports/cash-flow/", period, "reports.view"),
            new("reports/balance-sheet/", $"as_of={d(today)}", "reports.view"),
            new("reports/opening-balances/", "", "reports.view"),
        };
        var ids = db.Read(tx => new Dictionary<string, List<long>>
        {
            ["products"] = tx.Query<long>("SELECT id FROM products").ToList(),
            ["customers"] = tx.Query<long>("SELECT id FROM parties WHERE type = 'CUSTOMER'").ToList(),
            ["vendors"] = tx.Query<long>("SELECT id FROM parties WHERE type = 'VENDOR'").ToList(),
            ["sales"] = tx.Query<long>("SELECT id FROM sales").ToList(),
            ["purchases"] = tx.Query<long>("SELECT id FROM purchases").ToList(),
            ["customer-receipts"] = tx.Query<long>("SELECT id FROM customer_receipts").ToList(),
            ["vendor-payments"] = tx.Query<long>("SELECT id FROM vendor_payments").ToList(),
            ["expenses"] = tx.Query<long>("SELECT id FROM expenses").ToList(),
            ["cash-bank/contras"] = tx.Query<long>("SELECT id FROM contras").ToList(),
            ["journal/vouchers"] = tx.Query<long>("SELECT id FROM journal_vouchers").ToList(),
            ["cash-bank/accounts"] = tx.Query<long>("SELECT id FROM money_accounts").ToList(),
            ["journal/ledgers"] = tx.Query<long>("SELECT id FROM ledger_accounts").ToList(),
        });
        foreach (var id in ids["products"])
        {
            views.Add(new($"products/{id}/", "", "products.view"));
            views.Add(new($"inventory/{id}/", $"as_of={d(today)}", "inventory.view"));
        }
        foreach (var side in new[] { "customers", "vendors" })
            foreach (var id in ids[side])
            {
                views.Add(new($"{side}/{id}/", "", "parties.view"));
                views.Add(new($"{side}/{id}/summary/", "", "parties.view"));
                views.Add(new($"{side}/{id}/ledger/", all, "ledgers.view"));
                views.Add(new($"{side}/{id}/statement/", period, "ledgers.view"));
                views.Add(new($"{side}/{id}/aging/", "", "ledgers.view"));
            }
        foreach (var (route, perm) in new[]
                 {
                     ("sales", "sales.view"), ("purchases", "purchases.view"), ("customer-receipts", "receipts.view"),
                     ("vendor-payments", "payments.view"), ("expenses", "expenses.view"), ("cash-bank/contras", "cashbank.view"),
                     ("journal/vouchers", "journal.view"),
                 })
            views.AddRange(ids[route].Select(id => new View($"{route}/{id}/", "", perm)));
        views.AddRange(ids["cash-bank/accounts"].Select(id => new View($"cash-bank/accounts/{id}/book/", $"start_date={d(monthStart)}", "cashbank.view")));
        views.AddRange(ids["journal/ledgers"].Select(id => new View($"journal/ledgers/{id}/book/", "", "journal.view")));
        return views;
    }

    /// <summary>Firestore document id of a page: "customers/12/ledger/" -> "customers~12~ledger".</summary>
    public static string DocId(string path) => path.Trim('/').Replace('/', '~');

    private async Task SyncAsync(string slug, Database db, string book, FirebaseSession session, CancellationToken ct)
    {
        var views = Views(db);
        var uploaded = db.Read(tx => tx.Rows("SELECT id, hash, parts FROM cloud_views").ToDictionary(r => r.Str("id"), r => (Hash: r.Str("hash"), Parts: (int)r.Long("parts"))));

        // Fetch every page from this server (a few at a time) and keep the ones that changed.
        var changed = new ConcurrentBag<(string Id, string Hash, string[] Parts, string Perm)>();
        var seen = new ConcurrentDictionary<string, bool>();
        await Parallel.ForEachAsync(views, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (view, token) =>
        {
            var json = await FetchAsync(slug, view, token);
            if (json is null)
                return;
            var id = DocId(view.Path);
            seen[id] = true;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(view.Perm + "\n" + json)));
            if (uploaded.TryGetValue(id, out var old) && old.Hash == hash)
                return;
            changed.Add((id, hash, Split(Gzip(json)), view.Perm));
        });

        var now = DateTime.UtcNow;
        var batch = new List<JsonObject>();
        var batchViews = new List<(string Id, string Hash, int Parts)>();
        var batchChars = 0;

        async Task Flush()
        {
            if (batch.Count == 0)
                return;
            await firebase.CommitAsync(session.IdToken, batch, ct);
            db.Write(tx =>
            {
                foreach (var (id, hash, parts) in batchViews)
                    if (parts < 0)
                        tx.Execute("DELETE FROM cloud_views WHERE id = @id", new { id });
                    else
                        tx.Execute("INSERT INTO cloud_views (id, hash, parts) VALUES (@id, @hash, @parts) ON CONFLICT (id) DO UPDATE SET hash = @hash, parts = @parts",
                            new { id, hash, parts });
                return 0;
            });
            batch.Clear();
            batchViews.Clear();
            batchChars = 0;
        }

        // The book first, on its own: the rules check page writes against the stored book (members may have changed).
        await firebase.CommitAsync(session.IdToken, [BookDoc(db, book, session.Uid)], ct);
        foreach (var (id, hash, parts, perm) in changed)
        {
            if (batch.Count + parts.Length > 400 || batchChars + parts.Sum(p => p.Length) > 8_000_000)
                await Flush();
            for (var i = 0; i < parts.Length; i++)
                batch.Add(firebase.Update($"books/{book}/views/{(i == 0 ? id : $"{id}~~{i + 1}")}", new JsonObject
                {
                    ["z"] = Firebase.Str(parts[i]),
                    ["parts"] = Firebase.Int(parts.Length),
                    ["perm"] = Firebase.Str(perm),
                    ["at"] = Firebase.Time(now),
                }));
            // Parts no longer needed (the page got smaller).
            if (uploaded.TryGetValue(id, out var old))
                for (var i = parts.Length; i < old.Parts; i++)
                    batch.Add(firebase.Delete($"books/{book}/views/{id}~~{i + 1}"));
            batchViews.Add((id, hash, parts.Length));
            batchChars += parts.Sum(p => p.Length);
        }
        // Pages of records that no longer exist.
        foreach (var (id, old) in uploaded.Where(u => !seen.ContainsKey(u.Key)))
        {
            if (batch.Count + old.Parts > 400)
                await Flush();
            batch.Add(firebase.Delete($"books/{book}/views/{id}"));
            for (var i = 1; i < old.Parts; i++)
                batch.Add(firebase.Delete($"books/{book}/views/{id}~~{i + 1}"));
            batchViews.Add((id, "", -1));
        }
        await Flush();
        SetState(db, "pages", seen.Count.ToString());
    }

    /// <summary>The "data" of a page from this server, as JSON text; null when the page is not available.</summary>
    private async Task<string?> FetchAsync(string slug, View view, CancellationToken ct)
    {
        var url = $"{serverUrl().TrimEnd('/')}/api/{view.Path}{(view.Query.Length > 0 ? "?" + view.Query : "")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Internal-Key", InternalKey);
        request.Headers.Add("Cookie", $"{ApiServer.CompanyCookie}={slug}");
        using var response = await _self.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("data", out var data) ? data.GetRawText() : null;
    }

    private static string Gzip(string json)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize))
            gz.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(buffer.ToArray());
    }

    private static string[] Split(string text) =>
        Enumerable.Range(0, Math.Max(1, (text.Length + PartChars - 1) / PartChars))
            .Select(i => text.Substring(i * PartChars, Math.Min(PartChars, text.Length - i * PartChars))).ToArray();

    public override void Dispose()
    {
        _self.Dispose();
        base.Dispose();
    }
}
