using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Security;
using Accounting.Server.Cloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>
/// /api/cloud/: the online copy for phones ("Phone anywhere"). Administrators switch it on and see
/// or remove the phones; any signed-in user pairs their own phone while on the local network.
/// </summary>
public static class CloudApi
{
    private static CloudSync Sync(HttpContext h) =>
        ApiServer.Cloud(h) ?? throw new BusinessError("The online copy is not available in this program.", "CLOUD_UNAVAILABLE", 404);

    private static object Status(Api a, CloudSync sync)
    {
        var state = CloudSync.State(a.Db);
        var devices = a.Read(tx => tx.Rows(
            "SELECT d.uid, d.name, d.added_at, u.username FROM cloud_devices d JOIN users u ON u.id = d.user_id ORDER BY d.added_at").Select(r => new
        {
            uid = r.Str("uid"), name = r.Str("name"), username = r.Str("username"), added_at = r.Str("added_at"),
        }).ToList());
        return new
        {
            enabled = state.GetValueOrDefault("enabled") == "1",
            app_url = sync.Config.AppUrl,
            last_sync = state.GetValueOrDefault("last_sync"),
            last_error = state.GetValueOrDefault("last_error"),
            pages = int.TryParse(state.GetValueOrDefault("pages"), out var p) ? p : 0,
            devices,
        };
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("cloud", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            return Api.Ok(Status(a, Sync(h)));
        });

        api.MapPost("cloud/enable", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            var on = (await a.BodyAsync()).Bool("enabled") ?? true;
            var sync = Sync(h);
            sync.SetEnabled((string)h.Items["company"]!, on);
            return Api.Ok(Status(a, sync), on ? "Online copy switched on. The first upload starts in a few seconds." : "Online copy switched off.");
        });

        api.MapPost("cloud/sync", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            var sync = Sync(h);
            sync.SyncSoon((string)h.Items["company"]!);
            return Api.Ok(Status(a, sync), "Uploading changes now.");
        });

        api.MapDelete("cloud/devices/{uid}", (HttpContext h, string uid) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            var sync = Sync(h);
            sync.RemoveDevice((string)h.Items["company"]!, uid);
            return Api.Ok(Status(a, sync), "Phone removed. It can no longer open the online copy.");
        });

        // Any signed-in user: a one-time link that lets this phone open the online copy as this user.
        api.MapPost("cloud/pair", (HttpContext h) =>
        {
            var a = Api.Of(h);
            var user = a.User!;
            var me = JsonSerializer.Serialize(AuthApi.UserJson(user), Json.Options);
            var (code, url, expires) = Sync(h).StartPairing((string)h.Items["company"]!, user, me, Permissions.For(user.ToActor()));
            return Api.Ok(new { code, url, expires_at = expires.ToString("O") });
        });
    }
}
