using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounting.Server;
using Accounting.Server.Cloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Accounting.Tests;

/// <summary>The online copy for phones, against a small fake of the Firebase REST API.</summary>
public sealed class CloudTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "acct-cloud-" + Guid.NewGuid().ToString("N"));
    private WebApplication _fake = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly CookieContainer _cookies = new();

    /// <summary>Fake Firestore: document name (after .../documents/) -> fields.</summary>
    private readonly ConcurrentDictionary<string, JsonObject> _docs = new();
    private int _commits;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _fake = FakeFirebase();
        await _fake.StartAsync();
        var fake = _fake.Urls.First();
        var config = new CloudConfig("key", "p", "https://example.web.app", $"{fake}/auth", $"{fake}/token", $"{fake}/fs");
        _app = ApiServer.Build(new ServerOptions(Path.Combine(_folder, "company.db"), null, ["http://127.0.0.1:0"], Cloud: config));
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri(_app.Urls.First()) };

        await Get("/api/auth/csrf/");
        await Post("/api/companies/", new { name = "Himal Traders", setup = true, admin_username = "admin", admin_password = "Admin@12345", demo = true });
        await Post("/api/auth/login/", new { username = "admin", password = "Admin@12345" });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _fake.StopAsync();
        await _fake.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private WebApplication FakeFirebase()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        object Session() => new { localId = "pc-uid", idToken = "pc-token", refreshToken = "pc-refresh", expiresIn = "3600" };
        app.MapPost("/auth/accounts:signUp", () => Results.Json(Session()));
        app.MapPost("/token/token", () => Results.Json(new { user_id = "pc-uid", id_token = "pc-token", refresh_token = "pc-refresh", expires_in = "3600" }));
        app.MapPost("/fs/{**rest}", async (HttpContext h) =>
        {
            var body = JsonNode.Parse(await new StreamReader(h.Request.Body).ReadToEndAsync())!;
            Interlocked.Increment(ref _commits);
            foreach (var w in body["writes"]!.AsArray())
                if (w!["update"] is { } u)
                    _docs[Short(u["name"]!.GetValue<string>())] = u["fields"]!.AsObject().DeepClone().AsObject();
                else
                    _docs.TryRemove(Short(w["delete"]!.GetValue<string>()), out _);
            return Results.Json(new { });
        });
        app.MapGet("/fs/{**rest}", (string rest) =>
        {
            var prefix = Short(rest) + "/";
            var docs = _docs.Where(d => d.Key.StartsWith(prefix) && !d.Key[prefix.Length..].Contains('/'))
                .Select(d => new { name = "projects/p/databases/(default)/documents/" + d.Key, fields = d.Value }).ToList();
            return Results.Json(new { documents = docs });
        });
        return app;
    }

    private static string Short(string name) => name[(name.IndexOf("/documents/", StringComparison.Ordinal) + "/documents/".Length)..];

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (_cookies.GetCookies(_http.BaseAddress!)[ApiServer.CsrfCookie] is { } csrf)
            request.Headers.Add("X-CSRFToken", csrf.Value);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> Get(string path) => Send(HttpMethod.Get, path);
    private Task<JsonElement> Post(string path, object body) => Send(HttpMethod.Post, path, body);

    private CloudSync Sync => _app.Services.GetServices<IHostedService>().OfType<CloudSync>().Single();

    private string Book => _docs.Keys.Single(k => k.StartsWith("books/") && k.Count(c => c == '/') == 1)["books/".Length..];

    /// <summary>A page of the online copy, unpacked as the phone does.</summary>
    private JsonElement Page(string id)
    {
        var first = _docs[$"books/{Book}/views/{id}"];
        var parts = int.Parse(first["parts"]!["integerValue"]!.GetValue<string>());
        var text = string.Concat(Enumerable.Range(1, parts).Select(i =>
            _docs[$"books/{Book}/views/{(i == 1 ? id : $"{id}~~{i}")}"]["z"]!["stringValue"]!.GetValue<string>()));
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(text)), CompressionMode.Decompress);
        return JsonDocument.Parse(new StreamReader(gz, Encoding.UTF8).ReadToEnd()).RootElement.Clone();
    }

    [Fact]
    public async Task UploadsEveryPageOnceAndOnlyChangesAfterwards()
    {
        await Post("/api/cloud/enable/", new { enabled = true });
        await Sync.TickAsync("main", CancellationToken.None);

        var sales = Page("sales");
        var count = sales.GetProperty("count").GetInt32();
        Assert.True(count > 0);
        Assert.Equal(count, sales.GetProperty("results").GetArrayLength()); // the whole list, not one page
        var id = sales.GetProperty("results")[0].GetProperty("id").GetInt64();
        Assert.Equal(id, Page($"sales~{id}").GetProperty("id").GetInt64());
        Assert.Equal("sales.view", _docs[$"books/{Book}/views/sales"]["perm"]!["stringValue"]!.GetValue<string>());
        var customer = Page("customers").GetProperty("results")[0].GetProperty("id").GetInt64();
        Assert.Equal(JsonValueKind.Array, Page($"customers~{customer}~ledger").GetProperty("results").ValueKind);
        Assert.Equal("Himal Traders", Page("settings").GetProperty("business_name").GetString());

        // Nothing changed: the next round uploads nothing (no page commits).
        var views = _docs.Count;
        await Sync.TickAsync("main", CancellationToken.None);
        Assert.Equal(views, _docs.Count);

        var status = await Get("/api/cloud/");
        Assert.True(status.GetProperty("data").GetProperty("pages").GetInt32() > 30);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("data").GetProperty("last_error").ValueKind);
    }

    [Fact]
    public async Task PhoneJoinsOnlyWithTheCodeTheComputerGave()
    {
        await Post("/api/cloud/enable/", new { enabled = true });
        await Sync.TickAsync("main", CancellationToken.None);
        var pair = (await Post("/api/cloud/pair/", new { })).GetProperty("data");
        var code = pair.GetProperty("code").GetString()!;
        Assert.Contains($"/link?b={Book}&c={code}", pair.GetProperty("url").GetString());

        // The phone writes its request; a stranger guesses a code.
        _docs[$"books/{Book}/pairing/{code}"] = new JsonObject { ["uid"] = new JsonObject { ["stringValue"] = "phone-1" }, ["device"] = new JsonObject { ["stringValue"] = "iPhone" } };
        _docs[$"books/{Book}/pairing/{new string('0', 32)}"] = new JsonObject { ["uid"] = new JsonObject { ["stringValue"] = "stranger" } };
        await Sync.TickAsync("main", CancellationToken.None);

        var members = _docs[$"books/{Book}"]["members"]!["mapValue"]!["fields"]!.AsObject();
        Assert.Equal(["phone-1"], members.Select(m => m.Key));
        var perms = members["phone-1"]!["mapValue"]!["fields"]!["perms"]!["arrayValue"]!["values"]!.AsArray().Select(v => v!["stringValue"]!.GetValue<string>());
        Assert.Contains("sales.view", perms);
        Assert.DoesNotContain(_docs.Keys, k => k.Contains("/pairing/"));

        var devices = (await Get("/api/cloud/")).GetProperty("data").GetProperty("devices");
        Assert.Equal("iPhone", devices[0].GetProperty("name").GetString());

        // Removing the phone takes it out of the book.
        await Send(HttpMethod.Delete, "/api/cloud/devices/phone-1/");
        await Sync.TickAsync("main", CancellationToken.None);
        Assert.Empty(_docs[$"books/{Book}"]["members"]!["mapValue"]!["fields"]!.AsObject());
    }

    private static JsonObject Str(string v) => new() { ["stringValue"] = v };

    private async Task<string> LinkPhoneAsync()
    {
        await Post("/api/cloud/enable/", new { enabled = true });
        await Sync.TickAsync("main", CancellationToken.None);
        var code = (await Post("/api/cloud/pair/", new { })).GetProperty("data").GetProperty("code").GetString()!;
        _docs[$"books/{Book}/pairing/{code}"] = new JsonObject { ["uid"] = Str("phone-1"), ["device"] = Str("iPhone") };
        await Sync.TickAsync("main", CancellationToken.None);
        return _docs[$"books/{Book}"]["members"]!["mapValue"]!["fields"]!["phone-1"]!["mapValue"]!["fields"]!["prefix"]!["stringValue"]!.GetValue<string>();
    }

    private void PhoneSends(string number, string kind, object body) =>
        _docs[$"books/{Book}/outbox/{number}"] = new JsonObject
        {
            ["uid"] = Str("phone-1"), ["kind"] = Str(kind), ["body"] = Str(JsonSerializer.Serialize(body)), ["device"] = Str("iPhone"),
        };

    private string Result(string number, string field) => _docs[$"books/{Book}/results/{number}"][field]!.AsObject().First().Value!.ToString();

    [Fact]
    public async Task PhoneSalesAreBookedWithThePhonesOwnNumbers()
    {
        var prefix = await LinkPhoneAsync();
        Assert.Equal("M1", prefix);
        var customer = (await Get("/api/customers/")).GetProperty("data").GetProperty("results")[0].GetProperty("id").GetInt64();
        var product = (await Get("/api/products/")).GetProperty("data").GetProperty("results")[0];
        var date = DateTime.Today.ToString("yyyy-MM-dd");

        PhoneSends("M1-000001", "sale", new { party = customer, date, items = new[] { new { product = product.GetProperty("id").GetInt64(), quantity = "1", unit_price = (string?)null } } });
        PhoneSends("M1-000002", "sale", new { party = customer, date, items = new[] { new { product = product.GetProperty("id").GetInt64(), quantity = "999999", unit_price = (string?)null } } });
        PhoneSends("M1-R-000001", "receipt", new { party = customer, date, amount = "10", auto_allocate = true });
        await Sync.TickAsync("main", CancellationToken.None);

        // Booked with the phone's number; the phone learns the result; nothing is left in the outbox.
        Assert.Equal("booked", Result("M1-000001", "status"));
        var sale = (await Get($"/api/sales/{Result("M1-000001", "id")}/")).GetProperty("data");
        Assert.Equal("M1-000001", sale.GetProperty("invoice_number").GetString());
        Assert.Equal("booked", Result("M1-R-000001", "status"));
        Assert.Equal("M1-R-000001", (await Get($"/api/customer-receipts/{Result("M1-R-000001", "id")}/")).GetProperty("data").GetProperty("number").GetString());
        Assert.DoesNotContain(_docs.Keys, k => k.Contains("/outbox/"));

        // Not enough stock here: the bill keeps its number, waits as failed, and can be tried again.
        Assert.Equal("failed", Result("M1-000002", "status"));
        Assert.Contains("Insufficient stock", Result("M1-000002", "message"));
        var inbox = (await Get("/api/cloud/")).GetProperty("data").GetProperty("inbox");
        Assert.Contains(inbox.EnumerateArray(), i => i.GetProperty("number").GetString() == "M1-000002" && i.GetProperty("status").GetString() == "failed");

        // The phone's next numbers come after everything received (the failed one is not reused).
        var me = _docs[$"books/{Book}"]["members"]!["mapValue"]!["fields"]!["phone-1"]!["mapValue"]!["fields"]!;
        Assert.Equal("3", me["next_sale"]!["integerValue"]!.GetValue<string>());
        Assert.Equal("2", me["next_receipt"]!["integerValue"]!.GetValue<string>());

        // The same bill arriving twice is booked once.
        PhoneSends("M1-000001", "sale", new { party = customer, date, items = new[] { new { product = product.GetProperty("id").GetInt64(), quantity = "1", unit_price = (string?)null } } });
        await Sync.TickAsync("main", CancellationToken.None);
        var sales = (await Get("/api/sales/?search=M1-000001")).GetProperty("data").GetProperty("count").GetInt32();
        Assert.Equal(1, sales);
    }

    [Fact]
    public async Task PhoneEntryCanBeSwitchedOff()
    {
        await LinkPhoneAsync();
        Assert.True(_docs[$"books/{Book}"]["entry"]!["booleanValue"]!.GetValue<bool>());
        await Post("/api/cloud/entry/", new { enabled = false });
        await Sync.TickAsync("main", CancellationToken.None);
        Assert.False(_docs[$"books/{Book}"]["entry"]!["booleanValue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PairingNeedsTheOnlineCopySwitchedOn()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/pair/") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRFToken", _cookies.GetCookies(_http.BaseAddress!)[ApiServer.CsrfCookie]!.Value);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CLOUD_OFF", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InternalKeyIsIgnoredWithoutTheRightValue()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        anon.DefaultRequestHeaders.Add("X-Internal-Key", "guess");
        using var response = await anon.GetAsync("/api/sales/?page_size=all");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
