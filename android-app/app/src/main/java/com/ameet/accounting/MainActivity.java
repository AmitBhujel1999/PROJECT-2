package com.ameet.accounting;

import android.Manifest;
import android.app.Activity;
import android.app.DownloadManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inputmethod.EditorInfo;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.URLUtil;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import android.window.OnBackInvokedDispatcher;

/**
 * Thin Android wrapper around the accounting web app.
 *
 * All data stays on the server (the PC running start-windows.ps1, or a cloud
 * server); this app only remembers the server address and shows the site in a
 * WebView, so every phone and computer sees the same books.
 */
public class MainActivity extends Activity {

    private static final String PREFS = "accounting";
    private static final String KEY_SERVER = "server";
    private static final String DEFAULT_SERVER = "http://192.168.1.72:4173";
    private static final int NAVY = Color.rgb(0x17, 0x1c, 0x2b);
    private static final int BLUE = Color.rgb(0x26, 0x59, 0xa6);

    private FrameLayout root;
    private WebView web;
    private String server;
    private String pendingDownload;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        root = new FrameLayout(this);
        root.setBackgroundColor(NAVY);
        // Android 15+ draws apps edge to edge: keep content clear of the status
        // bar, navigation bar and on-screen keyboard.
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                Insets i = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                v.setPadding(i.left, i.top, i.right, i.bottom);
            } else {
                v.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                        insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            }
            return insets;
        });
        setContentView(root);
        setupBack();

        server = prefs().getString(KEY_SERVER, null);
        if (server != null && !validServer(server)) server = null;
        if (server == null) showSetup(null);
        else showWeb(state);
    }

    private SharedPreferences prefs() {
        return getSharedPreferences(PREFS, MODE_PRIVATE);
    }

    // ------------------------------------------------------------------ web view

    private void showWeb(Bundle state) {
        root.removeAllViews();
        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setTextZoom(100);
        s.setSupportMultipleWindows(false);
        s.setUserAgentString(s.getUserAgentString() + " AccountingApp/" + BuildConfigVersion());
        CookieManager.getInstance().setAcceptCookie(true);
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, true);
        web.addJavascriptInterface(new Bridge(), "AccountingApp");
        web.setWebViewClient(new Client());
        web.setDownloadListener((url, ua, disposition, mime, length) ->
                download(url, URLUtil.guessFileName(url, disposition, mime)));
        root.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (state == null || web.restoreState(state) == null) web.loadUrl(server + "/dashboard");
        // If the server never answers (wrong address, PC switched off), go back
        // to the address screen instead of leaving a blank page.
        WebView loading = web;
        root.postDelayed(() -> {
            if (web == loading && web.getProgress() < 100 && (web.getUrl() == null || web.getTitle() == null || web.getTitle().isEmpty())) {
                showSetup("No answer from " + server + ". Is the computer on and the app started?");
            }
        }, 20000);
    }

    private String BuildConfigVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (PackageManager.NameNotFoundException e) {
            return "1";
        }
    }

    @Override
    protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        if (web != null) web.saveState(out);
    }

    @Override
    protected void onPause() {
        super.onPause();
        CookieManager.getInstance().flush();
    }

    private class Client extends WebViewClient {
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            Uri url = request.getUrl();
            Uri base = Uri.parse(server);
            String path = url.getPath() == null ? "" : url.getPath();
            boolean same = url.getHost() != null && url.getHost().equals(base.getHost()) && url.getPort() == base.getPort();
            if (!same) {
                // Links to other sites open in the phone's browser.
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, url));
                } catch (Exception ignored) {
                }
                return true;
            }
            // WebView cannot show PDFs or save exports itself: hand them to Downloads.
            if (path.contains("/pdf/") || url.getQueryParameter("export") != null) {
                download(url.toString(), fileName(url));
                return true;
            }
            return false;
        }

        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            if (request.isForMainFrame()) showSetup("Could not reach " + server + "\n(" + error.getDescription() + ")");
        }
    }

    /** Called from the web app's More page ("Change server address"). */
    private class Bridge {
        @JavascriptInterface
        public void changeServer() {
            runOnUiThread(() -> showSetup(null));
        }

        @JavascriptInterface
        public String server() {
            return server;
        }
    }

    // ----------------------------------------------------------------- downloads

    private String fileName(Uri url) {
        String export = url.getQueryParameter("export");
        String[] parts = (url.getPath() == null ? "" : url.getPath()).split("/");
        StringBuilder name = new StringBuilder();
        for (String p : parts) {
            if (p.isEmpty() || p.equals("api") || p.equals("pdf")) continue;
            if (name.length() > 0) name.append('-');
            name.append(p);
        }
        if (name.length() == 0) name.append("document");
        return name + "." + (export != null ? export : "pdf");
    }

    private void download(String url, String name) {
        if (Build.VERSION.SDK_INT < 29
                && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            pendingDownload = url + "\n" + name;
            requestPermissions(new String[]{Manifest.permission.WRITE_EXTERNAL_STORAGE}, 1);
            return;
        }
        try {
            DownloadManager.Request r = new DownloadManager.Request(Uri.parse(url));
            String cookies = CookieManager.getInstance().getCookie(url);
            if (cookies != null) r.addRequestHeader("Cookie", cookies);
            r.addRequestHeader("User-Agent", web.getSettings().getUserAgentString());
            r.setTitle(name);
            r.setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);
            r.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name);
            ((DownloadManager) getSystemService(Context.DOWNLOAD_SERVICE)).enqueue(r);
            Toast.makeText(this, "Downloading " + name + "…", Toast.LENGTH_SHORT).show();
        } catch (Exception e) {
            Toast.makeText(this, "Download failed: " + e.getMessage(), Toast.LENGTH_LONG).show();
        }
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] permissions, int[] results) {
        if (pendingDownload != null && results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) {
            String[] p = pendingDownload.split("\n", 2);
            pendingDownload = null;
            download(p[0], p[1]);
        }
    }

    // --------------------------------------------------------------- setup page

    /** Ask for the server address (first launch, connection error, or on request). */
    private void showSetup(String error) {
        root.removeAllViews();
        if (web != null) {
            web.destroy();
            web = null;
        }
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setGravity(Gravity.CENTER_HORIZONTAL);
        int pad = dp(24);
        box.setPadding(pad, dp(48), pad, pad);

        TextView logo = new TextView(this);
        logo.setText("A");
        logo.setTextColor(Color.WHITE);
        logo.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26);
        logo.setTypeface(Typeface.DEFAULT_BOLD);
        logo.setGravity(Gravity.CENTER);
        logo.setBackground(rounded(BLUE, 14));
        box.addView(logo, new LinearLayout.LayoutParams(dp(56), dp(56)));

        box.addView(text("Accounting & Inventory", 20, Color.WHITE, true), wrap(dp(14)));
        box.addView(text("Connect to your accounting server", 14, Color.argb(170, 255, 255, 255), false), wrap(dp(4)));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(18), dp(18), dp(18));
        card.setBackground(rounded(Color.WHITE, 16));

        if (error != null) {
            TextView err = text(error, 13, Color.rgb(0xc0, 0x39, 0x2b), false);
            card.addView(err, wrap(0));
        }
        card.addView(text("Server address", 13, Color.rgb(0x5b, 0x62, 0x75), true), wrap(error != null ? dp(12) : 0));
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_TEXT_VARIATION_URI);
        input.setImeOptions(EditorInfo.IME_ACTION_GO);
        input.setText(server != null ? server : DEFAULT_SERVER);
        input.setSelectAllOnFocus(true);
        card.addView(input, wrap(dp(4)));
        card.addView(text("The address shown when you start the app on your computer, e.g. "
                + DEFAULT_SERVER + ". The phone must be on the same Wi-Fi (or Tailscale).", 12,
                Color.rgb(0x5b, 0x62, 0x75), false), wrap(dp(6)));

        Button connect = new Button(this);
        connect.setText(server != null && error != null ? "Retry" : "Connect");
        connect.setTextColor(Color.WHITE);
        connect.setAllCaps(false);
        connect.setBackground(rounded(BLUE, 10));
        card.addView(connect, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48)) {{
            topMargin = dp(16);
        }});

        Runnable go = () -> {
            String value = input.getText().toString().trim().replaceAll("/+$", "");
            if (!value.startsWith("http://") && !value.startsWith("https://")) value = "http://" + value;
            if (!validServer(value)) {
                input.setError("Enter an address like " + DEFAULT_SERVER);
                return;
            }
            server = value;
            prefs().edit().putString(KEY_SERVER, server).apply();
            showWeb(null);
        };
        connect.setOnClickListener(v -> go.run());
        input.setOnEditorActionListener((v, action, event) -> {
            go.run();
            return true;
        });

        box.addView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT) {{
            topMargin = dp(24);
        }});
        android.widget.ScrollView scroll = new android.widget.ScrollView(this);
        scroll.addView(box);
        root.addView(scroll);
    }

    /** http(s)://host[:port] with nothing after it. */
    private static boolean validServer(String value) {
        try {
            java.net.URL u = new java.net.URL(value);
            String path = u.getPath();
            return !u.getHost().isEmpty() && u.getHost().matches("[A-Za-z0-9.-]+")
                    && (path.isEmpty() || path.equals("/")) && u.getQuery() == null;
        } catch (java.net.MalformedURLException e) {
            return false;
        }
    }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(value);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private LinearLayout.LayoutParams wrap(int top) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.topMargin = top;
        return p;
    }

    private GradientDrawable rounded(int color, int radiusDp) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(dp(radiusDp));
        return d;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    // ---------------------------------------------------------------- back key

    private void goBack() {
        if (web != null && web.canGoBack()) web.goBack();
        else if (web == null && server != null) showWeb(null);
        else finish();
    }

    private void setupBack() {
        if (Build.VERSION.SDK_INT >= 33) {
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::goBack);
        }
    }

    @Override
    @SuppressWarnings("deprecation")
    public void onBackPressed() {
        goBack();
    }
}
