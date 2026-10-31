using System.Collections.Concurrent;
using System.Security.Cryptography;
using Accounting.Core;
using Accounting.Core.Security;
using Accounting.Server.Cloud;
using Accounting.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Accounting.Server;

/// <param name="MainCompanyFile">The program's company file (company "main").</param>
/// <param name="WebRoot">Folder with the built web screens (index.html); null serves the API only.</param>
/// <param name="Urls">Where to listen, e.g. "http://0.0.0.0:4173" for the phone on the same Wi-Fi.</param>
/// <param name="Today">Business date override (tests).</param>
/// <param name="Cloud">Firebase project for the online copy for phones; null switches the feature off.</param>
/// <param name="PickFolder">Shows a folder chooser on this computer (starting at the given folder) and returns the choice; null when there is no window.</param>
/// <param name="Restart">Closes and starts the program again (to finish a restore); null when the program cannot do that itself.</param>
public sealed record ServerOptions(string MainCompanyFile, string? WebRoot, string[] Urls, Func<DateOnly>? Today = null, CloudConfig? Cloud = null,
    Func<string?, string?>? PickFolder = null, Action? Restart = null);

/// <summary>Marks an endpoint that works without signing in (login, CSRF cookie, company list, health).</summary>
public sealed class AnonymousApi;

/// <summary>
/// The built-in web server. It answers the same JSON API the Django backend did, so the existing
/// web screens and the Android app work unchanged, and serves those screens from <see cref="ServerOptions.WebRoot"/>.
///
/// Security, as in Django: an HTTP-only session cookie, a CSRF cookie that must be echoed in the
/// X-CSRFToken header on every change, role permissions checked in the services, and a limit on
/// failed sign-ins per address.
/// </summary>
public static class ApiServer
{
    public const string SessionCookie = "acct_sessionid";
    public const string CsrfCookie = "acct_csrftoken";
    public const string CompanyCookie = "acct_company";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public static WebApplication Build(ServerOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(options.Urls);
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        var companies = new CompanyRegistry(options.MainCompanyFile, options.Today);
        WebApplication? built = null;
        CloudSync? cloud = null;
        if (options.Cloud is { } config)
        {
            cloud = new CloudSync(companies, new Firebase(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, config), () => SelfUrl(built!));
            builder.Services.AddHostedService(_ => cloud);
        }
        var backups = new Backups(companies);
        builder.Services.AddHostedService(_ => backups);
        var app = built = builder.Build();
        app.Use(ErrorsAsEnvelope);
        app.UseRouting();
        app.Use((context, next) =>
        {
            context.Items[nameof(Backups)] = backups;
            context.Items[nameof(ServerOptions)] = options;
            return Authenticate(context, next, companies, cloud);
        });
        var api = app.MapGroup("/api");
        Map(api);

        if (options.WebRoot is { } root && Directory.Exists(root))
            ServeScreens(app, root);
        return app;
    }

    /// <summary>This server's own address for loopback calls ("http://0.0.0.0:4173" -> "http://127.0.0.1:4173").</summary>
    private static string SelfUrl(WebApplication app)
    {
        var url = app.Urls.First();
        foreach (var any in new[] { "0.0.0.0", "[::]", "+", "*", "localhost" })
            url = url.Replace("://" + any + ":", "://127.0.0.1:");
        return url;
    }

    /// <summary>The online-copy sync of the server handling this request, or null when it is off.</summary>
    public static CloudSync? Cloud(HttpContext context) => context.Items[nameof(CloudSync)] as CloudSync;

    /// <summary>The backups of the server handling this request.</summary>
    public static Backups Backups(HttpContext context) => (Backups)context.Items[nameof(Server.Backups)]!;

    public static ServerOptions Options(HttpContext context) => (ServerOptions)context.Items[nameof(ServerOptions)]!;

    /// <summary>The companies of the server handling this request.</summary>
    public static CompanyRegistry Companies(HttpContext context) => (CompanyRegistry)context.Items[nameof(CompanyRegistry)]!;

    private static void Map(RouteGroupBuilder api)
    {
        api.MapGet("health", () => Api.Ok(new { status = "ok" })).WithMetadata(new AnonymousApi());
        AuthApi.Map(api);
        AdminApi.Map(api);
        CloudApi.Map(api);
        BackupApi.Map(api);
        ProductsApi.Map(api);
        PartiesApi.Map(api);
        BulkApi.Map(api);
        InventoryApi.Map(api);
        TradeApi.Map(api);
        PaymentsApi.Map(api);
        ExpensesApi.Map(api);
        CashBankApi.Map(api);
        JournalApi.Map(api);
        ReportsApi.Map(api);
        api.Map("{**rest}", () => Results.Json(new { success = false, error = new { code = "NOT_FOUND", message = "The requested record was not found." } }, Json.Options, statusCode: 404));
    }

    // ------------------------------------------------------------------ middleware

    private static async Task ErrorsAsEnvelope(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (BusinessError e) when (!context.Response.HasStarted)
        {
            object? details = e is ValidationError
                ? e.Details.ToDictionary(kv => kv.Key, kv => (object?)new[] { kv.Value })
                : e.Details.Count > 0 ? e.Details : null;
            var message = e is ValidationError v ? $"{Pretty(v.Field)}: {e.Message}" : e.Message;
            await Write(context, e.StatusCode, e.Code, message, details);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (!context.Response.HasStarted && e.SqliteErrorCode == 19)
        {
            // A database rule (CHECK / UNIQUE / trigger) stopped the change.
            await Write(context, 400, "INTEGRITY_ERROR", e.Message.Replace("SQLite Error 19: ", ""), null);
        }
        catch (Exception) when (!context.Response.HasStarted && context.Request.Path.StartsWithSegments("/api"))
        {
            await Write(context, 500, "SERVER_ERROR", "An unexpected error occurred.", null);
        }
    }

    private static string Pretty(string field) => field is "non_field_errors" or "__all__" ? "" : char.ToUpperInvariant(field[0]) + field[1..].Replace('_', ' ');

    private static Task Write(HttpContext context, int status, string code, string message, object? details)
    {
        context.Response.StatusCode = status;
        message = message.StartsWith(": ", StringComparison.Ordinal) ? message[2..] : message;
        object error = details is null ? new { code, message } : new { code, message, details };
        return context.Response.WriteAsJsonAsync(new { success = false, error }, Json.Options);
    }

    private static readonly ConcurrentDictionary<string, (int Count, DateTime Since)> FailedLogins = new();

    /// <summary>At most 10 failed sign-ins per address in 5 minutes.</summary>
    public static void CheckLoginRate(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (FailedLogins.TryGetValue(ip, out var f) && f.Count >= 10 && DateTime.UtcNow - f.Since < TimeSpan.FromMinutes(5))
            throw new BusinessError("Too many failed sign-ins. Try again in a few minutes.", "RATE_LIMITED", 429);
    }

    public static void RecordFailedLogin(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
        FailedLogins.AddOrUpdate(ip, _ => (1, DateTime.UtcNow),
            (_, f) => DateTime.UtcNow - f.Since > TimeSpan.FromMinutes(5) ? (1, DateTime.UtcNow) : (f.Count + 1, f.Since));
    }

    private static async Task Authenticate(HttpContext context, Func<Task> next, CompanyRegistry companies, CloudSync? cloud)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var slug = context.Request.Cookies[CompanyCookie] is { } c && companies.Exists(c) ? c : CompanyRegistry.MainSlug;
        var db = companies.Get(slug)!;
        User? user = null;
        if (context.Request.Cookies[SessionCookie] is { Length: > 0 } token)
            user = db.Read(tx => UserService.SessionUser(tx, token));
        else if (cloud is not null && context.Request.Headers["X-Internal-Key"].ToString() is { Length: > 0 } key
                 && context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
                 && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(CloudSync.InternalKey)))
        {
            // The online-copy sync: reading pages as the company's first administrator, or booking a
            // phone's sale as the phone's user.
            user = long.TryParse(context.Request.Headers["X-Internal-User"].ToString(), out var asUser)
                ? db.Read(tx => tx.Scalar<long?>("SELECT id FROM users WHERE id = @asUser AND is_active = 1", new { asUser }) is { } id ? UserService.Get(tx, id) : null)
                : db.Read(tx => tx.Scalar<long?>("SELECT id FROM users WHERE role = 'ADMIN' AND is_active = 1 ORDER BY id LIMIT 1") is { } adminId
                    ? UserService.Get(tx, adminId) : null);
            context.Items["internal"] = true;
        }
        context.Items[nameof(Api)] = new Api(context, db, user);
        context.Items["company"] = slug;
        context.Items[nameof(CompanyRegistry)] = companies;
        if (cloud is not null)
            context.Items[nameof(CloudSync)] = cloud;

        var anonymous = context.GetEndpoint()?.Metadata.GetMetadata<AnonymousApi>() is not null;
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        {
            var cookie = context.Request.Cookies[CsrfCookie];
            var header = context.Request.Headers["X-CSRFToken"].ToString();
            if (string.IsNullOrEmpty(cookie) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(cookie), System.Text.Encoding.UTF8.GetBytes(header)))
            {
                await Write(context, 403, "CSRF_FAILED", "CSRF verification failed. Refresh the page and try again.", null);
                return;
            }
        }
        if (user is null && !anonymous)
        {
            await Write(context, 401, "AUTHENTICATION_REQUIRED", "Authentication credentials were not provided.", null);
            return;
        }
        await next();
    }

    public static void IssueCsrf(HttpContext context)
    {
        if (!string.IsNullOrEmpty(context.Request.Cookies[CsrfCookie]))
            return;
        context.Response.Cookies.Append(CsrfCookie, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/" });
    }

    // ------------------------------------------------------------------ the web screens

    /// <summary>Serve the built screens; any other path gets the app page, which routes in the browser.</summary>
    private static void ServeScreens(WebApplication app, string root)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(root));
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".mjs"] = "text/javascript"; // pdf.js worker (PDF preview)
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                // Hashed build files never change; pages must always be fresh.
                ctx.Context.Response.Headers.CacheControl = ctx.File.Name.EndsWith(".html", StringComparison.Ordinal)
                    ? "no-cache" : ctx.Context.Request.Path.StartsWithSegments("/_app/immutable") ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
        var fallback = File.Exists(Path.Combine(root, "200.html")) ? "200.html" : "index.html";
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.XFrameOptions = "SAMEORIGIN";
            await context.Response.SendFileAsync(files.GetFileInfo(fallback));
        });
    }
}
