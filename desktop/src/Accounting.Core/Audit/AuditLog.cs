using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Core.Data;
using Accounting.Core.Security;

namespace Accounting.Core.Audit;

public static class AuditAction
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string Cancel = "CANCEL";
    public const string Activate = "ACTIVATE";
    public const string Deactivate = "DEACTIVATE";
    public const string Allocate = "ALLOCATE";
    public const string Login = "LOGIN";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string Logout = "LOGOUT";
    public const string PasswordChange = "PASSWORD_CHANGE";
    public const string PermissionChange = "PERMISSION_CHANGE";
}

public sealed record AuditEntry(
    long Id, long? UserId, string Username, string Action, string ModelName, string ObjectId,
    string ObjectRepr, DateTime Timestamp, string? IpAddress, string? BeforeData, string? AfterData);

public sealed record AuditFilter
{
    public string? Action { get; init; }
    public string? ModelName { get; init; }
    public string? ObjectId { get; init; }
    public long? UserId { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    /// <summary>Matches username, object, object id or model name.</summary>
    public string? Search { get; init; }
}

/// <summary>Append-only audit trail. A database trigger blocks every UPDATE and DELETE.</summary>
public static class AuditLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper), new DecimalAsString() },
    };

    /// <summary>JSON copy of an entity's fields, for the before/after columns.</summary>
    public static string? Snapshot(object? entity) => entity is null ? null : JsonSerializer.Serialize(entity, entity.GetType(), Json);

    public static void Record(Tx tx, Actor actor, string action, string modelName, object? objectId, string objectRepr,
        string? before = null, string? after = null)
    {
        tx.RequireWrite();
        tx.Execute(
            """
            INSERT INTO audit_log (user_id, username, action, model_name, object_id, object_repr, ip_address, before_data, after_data)
            VALUES (@UserId, @Username, @action, @modelName, @objectId, @objectRepr, @IpAddress, @before, @after)
            """,
            new
            {
                actor.UserId,
                actor.Username,
                action,
                modelName,
                objectId = objectId?.ToString() ?? "",
                objectRepr = objectRepr.Length > 255 ? objectRepr[..255] : objectRepr,
                actor.IpAddress,
                before,
                after,
            });
    }

    public static IReadOnlyList<AuditEntry> ForObject(Tx tx, string modelName, object objectId) =>
        tx.Rows("SELECT * FROM audit_log WHERE model_name = @modelName AND object_id = @id ORDER BY id",
                new { modelName, id = objectId.ToString() })
            .Select(Map).ToList();

    public static AuditEntry Get(Tx tx, long id) =>
        tx.Rows("SELECT * FROM audit_log WHERE id = @id", new { id }).Select(Map).FirstOrDefault() ?? throw new NotFoundError("The requested record was not found.");

    public static IReadOnlyList<AuditEntry> Recent(Tx tx, int limit = 100) =>
        tx.Rows("SELECT * FROM audit_log ORDER BY id DESC LIMIT @limit", new { limit }).Select(Map).ToList();

    /// <summary>The audit log viewer: newest first, filtered (dates are local calendar days).</summary>
    public static Common.Page<AuditEntry> List(Tx tx, AuditFilter filter, Common.PageRequest? page = null)
    {
        page ??= Common.PageRequest.First;
        const string where =
            """
            WHERE (@action IS NULL OR action = @action) AND (@modelName IS NULL OR model_name = @modelName)
              AND (@objectId IS NULL OR object_id = @objectId) AND (@userId IS NULL OR user_id = @userId)
              AND (@start IS NULL OR date(timestamp, 'localtime') >= @start) AND (@end IS NULL OR date(timestamp, 'localtime') <= @end)
              AND (@search IS NULL OR username LIKE @search OR object_repr LIKE @search OR object_id LIKE @search OR model_name LIKE @search)
            """;
        var args = new
        {
            action = filter.Action, modelName = filter.ModelName, objectId = filter.ObjectId, userId = filter.UserId,
            start = filter.Start, end = filter.End, search = Products.ProductService.LikePattern(filter.Search),
            limit = page.Size, offset = page.Offset,
        };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM audit_log {where}", args);
        var rows = tx.Rows($"SELECT * FROM audit_log {where} ORDER BY timestamp DESC, id DESC LIMIT @limit OFFSET @offset", args).Select(Map).ToList();
        return new Common.Page<AuditEntry>(rows, (int)count, page.Page, page.Size);
    }

    private static AuditEntry Map(Row r) => new(
        r.Long("id"), r.NullableLong("user_id"), r.Str("username"), r.Str("action"), r.Str("model_name"),
        r.Str("object_id"), r.Str("object_repr"), r.Timestamp("timestamp"), r.NullableStr("ip_address"),
        r.NullableStr("before_data"), r.NullableStr("after_data"));

    /// <summary>Decimals go out as strings ("65000.00"), like the Django snapshots.</summary>
    private sealed class DecimalAsString : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
