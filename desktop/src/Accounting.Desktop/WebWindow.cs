using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Accounting.Desktop;

/// <summary>
/// A window showing the program's web screens, with no browser bar. Links to other sites open in
/// the normal browser; pages the screens open in a new tab (PDFs, printouts) open in another window
/// that shares the sign-in.
/// </summary>
public sealed class WebWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly bool _isMain;
    private readonly Task _ready;

    /// <param name="start">Page to load; null for a window the screens asked to open (it loads itself).</param>
    public WebWindow(Uri? start)
    {
        _isMain = start is not null;
        Title = "Mero Lekhapal";
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x17, 0x2A);
        Content = _web;
        if (_isMain)
            RestorePlacement();
        else
        {
            Width = 1000;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _ready = Setup(start);
        Closing += (_, e) =>
        {
            if (!_isMain)
                return;
            if (!App.Restarting && MessageBox.Show(this, "Quit Mero Lekhapal?\n\nYou will need to sign in again next time.", "Mero Lekhapal",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SavePlacement();
        };
    }

    public CoreWebView2 Core => _web.CoreWebView2;

    private async Task Setup(Uri? start)
    {
        await _web.EnsureCoreWebView2Async(App.WebEnvironment);
        var s = Core.Settings;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        // App feel: no Ctrl+wheel / Ctrl+plus or touchpad pinch zoom.
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
#endif
        Core.DocumentTitleChanged += (_, _) => Title = WindowTitle(Core.DocumentTitle);
        Core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && !IsOurs(uri))
            {
                e.Cancel = true;
                App.OpenExternal(uri);
            }
        };
        Core.NewWindowRequested += OnNewWindow;
        Core.WindowCloseRequested += (_, _) => Close();
        if (start is not null)
        {
            // Sign in again every time the program starts: forget the last sign-in. Done on start
            // rather than on close so it also happens after a crash or power cut.
            Core.CookieManager.DeleteCookies(Accounting.Server.ApiServer.SessionCookie, start.AbsoluteUri);
#if DEBUG
            // ACCOUNTING_AUTOLOGIN=user:password signs in on start, for checking screens without typing (debug builds only).
            if (Environment.GetEnvironmentVariable("ACCOUNTING_AUTOLOGIN")?.Split(':', 2) is [var user, var password])
            {
                var open = Environment.GetEnvironmentVariable("ACCOUNTING_OPEN") ?? "/dashboard";
                var body = JsonSerializer.Serialize(new { username = user, password });
                await Core.AddScriptToExecuteOnDocumentCreatedAsync(
                    $$"""
                    (async () => {
                      if (sessionStorage.getItem('autologin')) return;
                      sessionStorage.setItem('autologin', '1');
                      await fetch('/api/auth/csrf/');
                      const token = document.cookie.match(/acct_csrftoken=([^;]+)/)?.[1] ?? '';
                      await fetch('/api/auth/login/', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRFToken': token }, body: {{JsonSerializer.Serialize(body)}} });
                      location.replace({{JsonSerializer.Serialize(open)}});
                    })();
                    """);
            }
#endif
            Core.Navigate(start.AbsoluteUri);
        }
    }

    private async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsOurs(uri))
        {
            e.Handled = true;
            if (uri is not null)
                App.OpenExternal(uri);
            return;
        }
        var deferral = e.GetDeferral();
        var child = new WebWindow(null) { Owner = this };
        child.Show();
        await child._ready;
        e.NewWindow = child.Core;
        e.Handled = true;
        deferral.Complete();
    }

    private static bool IsOurs(Uri uri) =>
        uri.Scheme is "about" or "blob" or "data" || (uri.Scheme == "http" && uri.Authority == App.Home.Authority);

    private string WindowTitle(string page)
    {
        var title = string.IsNullOrWhiteSpace(page) ? "Mero Lekhapal" : page.Replace(" · Mero Lekhapal", "") + " - Mero Lekhapal";
        return _isMain && App.PhoneAddress is { } phone ? $"{title}    (phone app: {phone})" : title;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ------------------------------------------------------------------ size and position

    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    private static string PlacementFile => Path.Combine(App.DataFolder, "window.json");

    private void RestorePlacement()
    {
        Width = Math.Min(1366, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.9);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        try
        {
            if (JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementFile)) is not { } p)
                return;
            // Only when the saved place is still on a screen (a monitor may have been unplugged).
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (p.Width >= 400 && p.Height >= 300 && screen.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                (Left, Top, Width, Height) = (p.Left, p.Top, p.Width, p.Height);
            }
            WindowState = p.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // First start, or an unreadable file: use the defaults.
        }
    }

    private void SavePlacement()
    {
        var r = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        try
        {
            File.WriteAllText(PlacementFile, JsonSerializer.Serialize(new Placement(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth bothering the user about.
        }
    }
}
