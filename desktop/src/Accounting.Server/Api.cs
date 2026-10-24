using System.Globalization;
using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Security;
using Microsoft.AspNetCore.Http;

namespace Accounting.Server;

/// <summary>
/// The current API request: company database, signed-in user, query string and JSON body, with the
/// same conventions as the Django REST API (envelope, decimal strings, pagination, error codes).
/// </summary>
public sealed class Api(HttpContext http, Database db, User? user)
{
    public HttpContext Http { get; } = http;
    public Database Db { get; } = db;
    public User? User { get; } = user;
    public Actor Actor => (User ?? throw new BusinessError("Authentication credentials were not provided.", "AUTHENTICATION_REQUIRED", 401))
        .ToActor(Http.Connection.RemoteIpAddress?.ToString());

    public static Api Of(HttpContext http) => (Api)http.Items[nameof(Api)]!;

    public void Require(string permission) => Permissions.Require(Actor, permission);

    public bool Can(string permission) => User is not null && Permissions.Has(Actor, permission);

    public T Read<T>(Func<Tx, T> work) => Db.Read(work);

    public T Write<T>(Func<Tx, Actor, T> work)
    {
        var actor = Actor;
        return Db.Write(tx => work(tx, actor));
    }

    public BusinessSettings Settings => Db.Read(BusinessSettings.Get);

    // ------------------------------------------------------------------ query string

    public string? Q(string name) => Http.Request.Query.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v.ToString()) ? v.ToString() : null;

    public DateOnly? Date(string name, DateOnly? fallback = null)
    {
        var raw = Q(name);
        if (raw is null)
            return fallback;
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ValidationError(name, "Enter a valid date (YYYY-MM-DD).");
    }

    public long? Int(string name) =>
        Q(name) is { } raw ? long.TryParse(raw, out var v) ? v : throw new ValidationError(name, "Must be a whole number.") : null;

    public bool? Bool(string name) => Q(name)?.ToLowerInvariant() switch { null => null, "true" or "1" => true, "false" or "0" => false, _ => null };

    public PageRequest Page => new((int)(Int("page") ?? 1), (int?)Int("page_size"));

    // ------------------------------------------------------------------ JSON body

    private JsonElement? _body;

    public async Task<JsonElement> BodyAsync()
    {
        if (_body is { } b)
            return b;
        if (Http.Request.ContentLength is 0 or null && !Http.Request.Headers.ContentType.ToString().Contains("json"))
            return (_body = JsonDocument.Parse("{}").RootElement).Value;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Http.Request.Body);
            _body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BusinessError("JSON parse error.", "PARSE_ERROR");
        }
        return _body.Value;
    }

    // ------------------------------------------------------------------ responses

    public static IResult Ok(object? data, string message = "", int status = 200) =>
        Results.Json(new { success = true, data, message }, Json.Options, statusCode: status);

    public static IResult Created(object? data, string message = "Created successfully.") => Ok(data, message, 201);

    /// <summary>A page of results in the Django pagination shape, with optional extra keys (e.g. summary).</summary>
    public static IResult Paged<T>(Page<T> page, Func<T, object> row, IDictionary<string, object?>? extra = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["results"] = page.Results.Select(row).ToList(),
            ["count"] = page.Count,
            ["page"] = page.PageNumber,
            ["page_size"] = page.PageSize,
            ["total_pages"] = page.TotalPages,
        };
        if (extra is not null)
            foreach (var (k, v) in extra)
                data[k] = v;
        return Ok(data);
    }

    /// <summary>Paginate rows that were computed in memory (ledgers, aging, ...).</summary>
    public IResult PagedList<T>(IReadOnlyList<T> rows, Func<T, object> row, IDictionary<string, object?>? extra = null)
    {
        var request = Page;
        var page = new Page<T>(rows.Skip(request.Offset).Take(request.Size).ToList(), rows.Count, request.Page, request.Size);
        if (page.Results.Count == 0 && request.Page > 1)
            throw new NotFoundError("Invalid page.");
        return Paged(page, row, extra);
    }

    public static IResult File(byte[] content, string contentType, string fileName, bool inline = false)
    {
        return new FileResult(content, contentType, fileName, inline);
    }

    private sealed class FileResult(byte[] content, string contentType, string fileName, bool inline) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = contentType;
            context.Response.Headers.ContentDisposition = $"{(inline ? "inline" : "attachment")}; filename=\"{fileName}\"";
            context.Response.Headers.CacheControl = "no-store";
            // Inline PDFs may be shown inside the app's own pages.
            context.Response.Headers.XFrameOptions = inline ? "SAMEORIGIN" : "DENY";
            if (inline)
                context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'self'";
            await context.Response.Body.WriteAsync(content);
        }
    }
}

/// <summary>JSON text for API values: money "1000.00", quantities "2.500", rates "13.00", ISO dates.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string M(decimal value) => Money.Round(value).ToString("0.00", CultureInfo.InvariantCulture);
    public static string? M(decimal? value) => value is { } v ? M(v) : null;
    public static string Q(decimal value) => Money.Qty(value).ToString("0.000", CultureInfo.InvariantCulture);
    public static string R(decimal value) => Money.Rate(value).ToString("0.00", CultureInfo.InvariantCulture);
    public static string D(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? D(DateOnly? value) => value is { } d ? D(d) : null;

    /// <summary>A stored UTC time as local ISO-8601 with offset, like Django.</summary>
    public static string T(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    public static string? T(DateTime? utc) => utc is { } t ? T(t) : null;
}

/// <summary>Reading fields of a JSON request body with DRF-style validation messages.</summary>
public static class Body
{
    public static bool Has(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);

    private static JsonElement? Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name) => Get(e, name) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    public static string ReqStr(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (s is null)
            throw new ValidationError(name, "This field is required.");
        if (s.Trim().Length == 0)
            throw new ValidationError(name, "This field may not be blank.");
        return s;
    }

    public static decimal? Dec(this JsonElement e, string name)
    {
        if (Get(e, name) is not { } v)
            return null;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        if (text.Trim().Length == 0)
            return null;
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ValidationError(name, "A valid number is required.");
    }

    public static decimal ReqDec(this JsonElement e, string name) => e.Dec(name) ?? throw new ValidationError(name, "This field is required.");

    public static long? Long(this JsonElement e, string name)
    {
        if (Get(e, name) is not { } v)
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n))
            return n;
        if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0)
            return null;
        throw new ValidationError(name, "A valid integer is required.");
    }

    public static long ReqLong(this JsonElement e, string name) => e.Long(name) ?? throw new ValidationError(name, "This field is required.");

    public static bool? Bool(this JsonElement e, string name) => Get(e, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()!.ToLowerInvariant() is "true" or "1" or "yes",
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => null,
        }
        : null;

    public static DateOnly? Date(this JsonElement e, string name) =>
        e.Str(name) is { Length: > 0 } s
            ? DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new ValidationError(name, "Date has wrong format. Use one of these formats instead: YYYY-MM-DD.")
            : null;

    public static DateOnly ReqDate(this JsonElement e, string name) => e.Date(name) ?? throw new ValidationError(name, "This field is required.");

    public static IEnumerable<JsonElement> List(this JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : [];

    /// <summary>The value with a fallback, for partial updates (PATCH).</summary>
    public static string StrOr(this JsonElement e, string name, string current) => e.Has(name) ? e.Str(name) ?? "" : current;
    public static decimal DecOr(this JsonElement e, string name, decimal current) => e.Has(name) ? e.Dec(name) ?? 0 : current;
    public static bool BoolOr(this JsonElement e, string name, bool current) => e.Has(name) ? e.Bool(name) ?? current : current;
}
