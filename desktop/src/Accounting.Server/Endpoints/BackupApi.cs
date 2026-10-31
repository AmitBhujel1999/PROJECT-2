using System.Net;
using Accounting.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>
/// /api/backup/: copies of the books in a folder of the user's choice, and restoring one. Only for
/// administrators, and only on the computer itself (not from a phone): these touch the PC's files.
/// </summary>
public static class BackupApi
{
    private static Api Admin(HttpContext h)
    {
        var a = Api.Of(h);
        a.Require("users.manage");
        if (h.Items.ContainsKey("internal") || h.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
            throw new BusinessError("Backups can only be managed on the computer running Mero Lekhapal.", "PC_ONLY", 403);
        return a;
    }

    private static object Status(HttpContext h)
    {
        var backups = ApiServer.Backups(h);
        var options = ApiServer.Options(h);
        var slug = (string)h.Items["company"]!;
        return new
        {
            folder = backups.Folder,
            target = backups.Target,
            last_backup = backups.LastUtc is { } t ? Json.T(t) : null,
            last_error = backups.LastError,
            keep_days = Backups.Keep,
            can_pick = options.PickFolder is not null,
            can_restart = options.Restart is not null,
            restore_pending = backups.RestorePending(slug),
            suggestions = Backups.Suggestions().Select(s => new { label = s.Label, path = s.Path }).ToList(),
            files = backups.List(slug).Select(b => new
            {
                name = b.Name,
                date = Json.D(b.Date),
                before_restore = b.Name.Contains("-before-restore-"),
                size = b.Size,
                saved_at = Json.T(b.ModifiedUtc),
            }).ToList(),
        };
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("backup", (HttpContext h) =>
        {
            Admin(h);
            return Api.Ok(Status(h));
        });

        api.MapPost("backup/folder", async (HttpContext h) =>
        {
            var a = Admin(h);
            var folder = (await a.BodyAsync()).Str("folder");
            var backups = ApiServer.Backups(h);
            backups.SetFolder(folder);
            if (backups.Folder is null)
                return Api.Ok(Status(h), "Backups switched off.");
            backups.RunAll();
            return Api.Ok(Status(h), "Backup folder saved. The first backup is done.");
        });

        // Shows Windows' folder chooser on the PC screen; the answer is null when the user cancels.
        api.MapPost("backup/pick", (HttpContext h) =>
        {
            Admin(h);
            var pick = ApiServer.Options(h).PickFolder
                       ?? throw new BusinessError("Type the folder instead.", "PICK_UNAVAILABLE", 400);
            return Api.Ok(new { folder = pick(ApiServer.Backups(h).Folder) });
        });

        api.MapPost("backup/now", (HttpContext h) =>
        {
            Admin(h);
            if (!ApiServer.Backups(h).RunAll())
                throw new BusinessError("Choose a backup folder first.", "NO_FOLDER", 400);
            return Api.Ok(Status(h), "Backup done.");
        });

        api.MapPost("backup/restore", async (HttpContext h) =>
        {
            var a = Admin(h);
            var name = (await a.BodyAsync()).ReqStr("name");
            var slug = (string)h.Items["company"]!;
            ApiServer.Backups(h).Restore(slug, name);
            if (ApiServer.Options(h).Restart is { } restart)
            {
                // Answer first, then close: the screen shows the message while the program restarts.
                _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => restart());
                return Api.Ok(Status(h), "Mero Lekhapal is restarting to put the backup in place. Sign in again when it opens.");
            }
            return Api.Ok(Status(h), "Close Mero Lekhapal and start it again to put the backup in place.");
        });
    }
}
