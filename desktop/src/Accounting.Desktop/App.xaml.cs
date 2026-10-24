using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Accounting.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;

namespace Accounting.Desktop;

/// <summary>
/// The desktop program: starts the built-in server (the same API and web screens the Android app
/// uses) and shows those screens in its own window. One program per company file; starting it again
/// brings the open window to the front.
/// </summary>
public partial class App : Application
{
    public const int DefaultPort = 4173;
    private WebApplication? _server;
    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    /// <summary>Where the window loads the screens from.</summary>
    public static Uri Home { get; private set; } = null!;

    /// <summary>Address to type in the phone app, or null when the phone cannot connect (no network, --local-only).</summary>
    public static string? PhoneAddress { get; private set; }

    public static CoreWebView2Environment WebEnvironment { get; private set; } = null!;

    /// <summary>%LOCALAPPDATA%\Accounting: company files, window position and the browser engine's data.</summary>
    public static string DataFolder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mero Lekhapal");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var file = Path.GetFullPath(Arg(e.Args, "--file") ?? Path.Combine(DataFolder, "company.db"));
        var port = int.TryParse(Arg(e.Args, "--port"), out var p) ? p : DefaultPort;
        var localOnly = e.Args.Contains("--local-only");

        if (!TakeOver(file))
        {
            Shutdown();
            return;
        }

        var web = Path.Combine(AppContext.BaseDirectory, "web");
        try
        {
            _server = ApiServer.Build(new ServerOptions(file, web, [$"http://{(localOnly ? "127.0.0.1" : "0.0.0.0")}:{port}"],
                Cloud: Accounting.Server.Cloud.CloudConfig.MeroLekhapal));
            await _server.StartAsync();
        }
        catch (IOException ex) when (ex.InnerException is SocketException or null)
        {
            Fail($"Another program is already using port {port}, so Mero Lekhapal cannot start.\n\n" +
                 $"Close that program, or start Mero Lekhapal with a different port (--port 4174).\n\n{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Fail($"The company file could not be opened:\n{file}\n\n{ex.Message}");
            return;
        }
        Home = new Uri($"http://127.0.0.1:{port}/");
        PhoneAddress = localOnly ? null : LanAddress() is { } ip ? $"http://{ip}:{port}" : null;

        try
        {
            // No touchpad "rubber band" scrolling past the edges of the screens.
            var options = new CoreWebView2EnvironmentOptions("--disable-features=ElasticOverscroll");
            WebEnvironment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "WebView2"), options);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            const string download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            if (MessageBox.Show("Mero Lekhapal needs the Microsoft Edge WebView2 Runtime, which is missing on this computer.\n\n" +
                                "Open the Microsoft download page now?", "Mero Lekhapal", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenExternal(new Uri(download));
            Shutdown(1);
            return;
        }

        var window = new WebWindow(Home);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _server?.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Closing anyway.
        }
        _showSignal?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// True when this is the only program open on the file. Otherwise it asks the open one to show
    /// its window and returns false.
    /// </summary>
    private bool TakeOver(string file)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..16];
        _single = new Mutex(true, $"Local\\Accounting-{id}", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\Accounting-show-{id}");
        if (!first)
        {
            _showSignal.Set();
            return false;
        }
        var signal = _showSignal;
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Dispatcher.BeginInvoke(() => (MainWindow as WebWindow)?.BringToFront());
            }
        }) { IsBackground = true, Name = "Show window" }.Start();
        return true;
    }

    private void Fail(string message)
    {
        MessageBox.Show(message, "Mero Lekhapal", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    /// <summary>This computer's address on the local network (Wi-Fi or cable), for the phone app.</summary>
    private static string? LanAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.GetIPProperties())
            .Where(ip => ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(ip => ip.UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
    }

    public static void OpenExternal(Uri uri)
    {
        if (uri.Scheme is "http" or "https" or "mailto" or "tel")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
