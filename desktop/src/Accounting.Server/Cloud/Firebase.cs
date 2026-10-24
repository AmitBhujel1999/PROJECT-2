using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accounting.Server.Cloud;

/// <summary>Where the online copy lives. The API key of a Firebase web app is public by design; the
/// security rules (firebase/firestore.rules) decide who may read what.</summary>
/// <param name="AuthUrl">Identity Toolkit base (anonymous sign-up), replaceable in tests.</param>
/// <param name="TokenUrl">Secure Token base (refresh), replaceable in tests.</param>
/// <param name="FirestoreUrl">Firestore REST base, replaceable in tests.</param>
public sealed record CloudConfig(
    string ApiKey,
    string ProjectId,
    string AppUrl,
    string AuthUrl = "https://identitytoolkit.googleapis.com/v1",
    string TokenUrl = "https://securetoken.googleapis.com/v1",
    string FirestoreUrl = "https://firestore.googleapis.com/v1")
{
    /// <summary>The Mero Lekhapal Firebase project.</summary>
    public static readonly CloudConfig MeroLekhapal = new(
        "AIzaSyBab1tS3IgJeMkPzym9NOLzVVuqTw3hLLc", "mero-lekhapal", "https://mero-lekhapal.web.app");

    public string DocumentsPath => $"projects/{ProjectId}/databases/(default)/documents";
}

/// <summary>A signed-in (anonymous) Firebase account.</summary>
public sealed record FirebaseSession(string Uid, string IdToken, string RefreshToken, DateTime ExpiresUtc);

/// <summary>The few Firebase REST calls the sync needs: anonymous accounts, batched writes, reads.</summary>
public sealed class Firebase(HttpClient http, CloudConfig config)
{
    public CloudConfig Config => config;

    public async Task<FirebaseSession> SignUpAnonymousAsync(CancellationToken ct)
    {
        var r = await PostJson($"{config.AuthUrl}/accounts:signUp?key={config.ApiKey}", new JsonObject { ["returnSecureToken"] = true }, null, ct);
        return new FirebaseSession(r["localId"]!.GetValue<string>(), r["idToken"]!.GetValue<string>(), r["refreshToken"]!.GetValue<string>(),
            DateTime.UtcNow.AddSeconds(int.Parse(r["expiresIn"]!.GetValue<string>()) - 120));
    }

    public async Task<FirebaseSession> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
        });
        using var response = await http.PostAsync($"{config.TokenUrl}/token?key={config.ApiKey}", content, ct);
        var r = await Read(response, ct);
        return new FirebaseSession(r["user_id"]!.GetValue<string>(), r["id_token"]!.GetValue<string>(), r["refresh_token"]!.GetValue<string>(),
            DateTime.UtcNow.AddSeconds(int.Parse(r["expires_in"]!.GetValue<string>()) - 120));
    }

    /// <summary>Atomic batch of writes (Firestore allows 500 per commit).</summary>
    public Task CommitAsync(string idToken, IReadOnlyList<JsonObject> writes, CancellationToken ct) =>
        PostJson($"{config.FirestoreUrl}/{config.DocumentsPath}:commit", new JsonObject { ["writes"] = new JsonArray([.. writes]) }, idToken, ct);

    /// <summary>The documents of a collection (only names and fields), or empty.</summary>
    public async Task<List<JsonObject>> ListAsync(string idToken, string collection, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.FirestoreUrl}/{config.DocumentsPath}/{collection}?pageSize=300");
        request.Headers.Authorization = new("Bearer", idToken);
        using var response = await http.SendAsync(request, ct);
        var r = await Read(response, ct);
        return r["documents"] is JsonArray docs ? docs.OfType<JsonObject>().ToList() : [];
    }

    // ------------------------------------------------------------------ document helpers

    public string DocName(string path) => $"{config.DocumentsPath}/{path}";

    public JsonObject Update(string path, JsonObject fields) =>
        new() { ["update"] = new JsonObject { ["name"] = DocName(path), ["fields"] = fields } };

    public JsonObject Delete(string path) => new() { ["delete"] = DocName(path) };

    public static JsonObject Str(string value) => new() { ["stringValue"] = value };
    public static JsonObject Int(long value) => new() { ["integerValue"] = value.ToString() };
    public static JsonObject Bool(bool value) => new() { ["booleanValue"] = value };
    public static JsonObject Time(DateTime utc) => new() { ["timestampValue"] = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") };
    public static JsonObject Array(IEnumerable<string> values) =>
        new() { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray([.. values.Select(v => (JsonNode)Str(v))]) } };
    public static JsonObject Map(JsonObject fields) => new() { ["mapValue"] = new JsonObject { ["fields"] = fields } };

    public static string? FieldString(JsonObject doc, string field) =>
        doc["fields"]?[field]?["stringValue"]?.GetValue<string>();

    // ------------------------------------------------------------------ transport

    private async Task<JsonObject> PostJson(string url, JsonObject body, string? idToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idToken is not null)
            request.Headers.Authorization = new("Bearer", idToken);
        using var response = await http.SendAsync(request, ct);
        return await Read(response, ct);
    }

    private static async Task<JsonObject> Read(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                message = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException)
            {
                // Not JSON: keep the text.
            }
            throw new CloudError($"Firebase answered {(int)response.StatusCode}: {message}");
        }
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }
}

public sealed class CloudError(string message) : Exception(message);
