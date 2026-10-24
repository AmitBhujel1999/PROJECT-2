using Accounting.Core.Security;
using Accounting.Server;

// Runs the built-in server on its own (no window): for testing the web screens, or running on a
// PC without anyone signed in. Usage:
//   Accounting.Host --file company.db --web ..\frontend\build-static --urls http://0.0.0.0:4173 [--admin user:password] [--today 2026-09-27] [--cloud]
string? Arg(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var file = Arg("--file") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mero Lekhapal", "company.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
Func<DateOnly>? today = Arg("--today") is { } t ? () => DateOnly.Parse(t) : null;
// --cloud switches on the online copy for phones (Firebase), as in the desktop program.
var cloud = args.Contains("--cloud") ? Accounting.Server.Cloud.CloudConfig.MeroLekhapal : null;
var app = ApiServer.Build(new ServerOptions(file, Arg("--web"), [Arg("--urls") ?? "http://127.0.0.1:4173"], today, cloud));

// --admin user:password creates the first administrator in an empty company file.
if (Arg("--admin")?.Split(':', 2) is [var user, var password])
{
    var db = new CompanyRegistry(file, today).Main;
    if (!db.Read(UserService.AnyUsers))
        db.Write(tx => UserService.CreateFirstAdmin(tx, user, password));
}

Console.WriteLine($"Mero Lekhapal server: {string.Join(", ", app.Urls.DefaultIfEmpty(Arg("--urls") ?? "http://127.0.0.1:4173"))}  ·  company file {Path.GetFullPath(file)}");
app.Run();
