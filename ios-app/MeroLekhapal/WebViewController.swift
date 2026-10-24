import UIKit
import WebKit

/// The Mero Lekhapal screens from the computer. PDFs and exports are downloaded and handed to the
/// share sheet (Save to Files, Print, WhatsApp...); window.print() prints the current page.
final class WebViewController: UIViewController, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler {
    var onProblem: ((String) -> Void)?
    var onChangeServer: (() -> Void)?

    private let server: String
    private var web: WKWebView!
    private var loadedOnce = false

    init(server: String) {
        self.server = server
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor(named: "Navy")

        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()
        let content = WKUserContentController()
        // Same bridge as the Android app (used by the More page: "Change server address"),
        // plus window.print().
        let serverJSON = String(data: try! JSONEncoder().encode(server), encoding: .utf8)!
        let bridge = """
        window.AccountingApp = {
          server: function () { return \(serverJSON); },
          changeServer: function () { window.webkit.messageHandlers.app.postMessage('changeServer'); }
        };
        window.print = function () { window.webkit.messageHandlers.app.postMessage('print'); };
        """
        content.addUserScript(WKUserScript(source: bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        content.add(self, name: "app")
        config.userContentController = content
        config.applicationNameForUserAgent = "AccountingApp/1.0 MeroLekhapal-iOS"

        web = WKWebView(frame: .zero, configuration: config)
        web.navigationDelegate = self
        web.uiDelegate = self
        web.allowsBackForwardNavigationGestures = true
        web.isOpaque = false
        web.backgroundColor = UIColor(named: "Navy")
        web.scrollView.bounces = false
        web.scrollView.contentInsetAdjustmentBehavior = .never
        web.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(web)
        let guide = view.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            web.topAnchor.constraint(equalTo: guide.topAnchor),
            web.bottomAnchor.constraint(equalTo: guide.bottomAnchor),
            web.leadingAnchor.constraint(equalTo: guide.leadingAnchor),
            web.trailingAnchor.constraint(equalTo: guide.trailingAnchor),
        ])

        if let url = URL(string: server + "/dashboard") {
            web.load(URLRequest(url: url, cachePolicy: .useProtocolCachePolicy, timeoutInterval: 15))
        }
    }

    deinit {
        web?.configuration.userContentController.removeScriptMessageHandler(forName: "app")
    }

    // MARK: - Bridge

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        switch message.body as? String {
        case "changeServer":
            onChangeServer?()
        case "print":
            let printer = UIPrintInteractionController.shared
            let info = UIPrintInfo(dictionary: nil)
            info.jobName = web.title ?? "Mero Lekhapal"
            info.outputType = .general
            printer.printInfo = info
            printer.printFormatter = web.viewPrintFormatter()
            printer.present(animated: true)
        default:
            break
        }
    }

    // MARK: - Navigation

    private func isOurs(_ url: URL) -> Bool {
        guard let base = URL(string: server) else { return false }
        return url.host == base.host && (url.port ?? 80) == (base.port ?? 80)
    }

    /// PDFs and CSV/Excel exports: the web view cannot save them, so download and share.
    private func isDocument(_ url: URL) -> Bool {
        let path = url.path
        let query = url.query ?? ""
        return path.contains("/pdf") || path.contains("/bulk/template") || query.contains("export=")
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url else { return decisionHandler(.allow) }
        if url.scheme == "about" || url.scheme == "blob" || url.scheme == "data" { return decisionHandler(.allow) }
        if !isOurs(url) {
            // Links to other sites open in Safari.
            UIApplication.shared.open(url)
            return decisionHandler(.cancel)
        }
        if isDocument(url) {
            download(url)
            return decisionHandler(.cancel)
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        let disposition = (navigationResponse.response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Disposition") ?? ""
        if let url = navigationResponse.response.url, disposition.lowercased().hasPrefix("attachment") {
            download(url)
            return decisionHandler(.cancel)
        }
        decisionHandler(.allow)
    }

    /// target="_blank" links and window.open(): documents go to the share sheet, pages open here.
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url {
            if !isOurs(url) {
                UIApplication.shared.open(url)
            } else if isDocument(url) {
                download(url)
            } else {
                webView.load(URLRequest(url: url))
            }
        }
        return nil
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        loadedOnce = true
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let e = error as NSError
        if e.domain == NSURLErrorDomain && e.code == NSURLErrorCancelled { return }
        // "Frame load interrupted" when a navigation turns into a download.
        if e.domain == "WebKitErrorDomain" && e.code == 102 { return }
        if loadedOnce {
            alert("Could not reach the computer", "\(e.localizedDescription)\n\nIs Mero Lekhapal open on the computer?")
        } else {
            onProblem?("Could not reach \(server)\n\(e.localizedDescription)\n\nIs the computer on, Mero Lekhapal open, and the iPhone on the same Wi-Fi?")
        }
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        webView.reload()
    }

    // MARK: - JavaScript dialogs (confirm "Cancel this invoice?" etc.)

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let a = UIAlertController(title: nil, message: message, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler() })
        present(a, animated: true)
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let a = UIAlertController(title: nil, message: message, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: "Cancel", style: .cancel) { _ in completionHandler(false) })
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler(true) })
        present(a, animated: true)
    }

    func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String, defaultText: String?,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (String?) -> Void) {
        let a = UIAlertController(title: nil, message: prompt, preferredStyle: .alert)
        a.addTextField { $0.text = defaultText }
        a.addAction(UIAlertAction(title: "Cancel", style: .cancel) { _ in completionHandler(nil) })
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler(a.textFields?.first?.text) })
        present(a, animated: true)
    }

    // MARK: - Downloads

    private func download(_ url: URL) {
        let spinner = UIActivityIndicatorView(style: .large)
        spinner.color = .white
        spinner.backgroundColor = UIColor(white: 0, alpha: 0.4)
        spinner.frame = view.bounds
        spinner.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        spinner.startAnimating()
        view.addSubview(spinner)

        // Send the web view's sign-in cookies with the download.
        web.configuration.websiteDataStore.httpCookieStore.getAllCookies { cookies in
            var request = URLRequest(url: url, timeoutInterval: 60)
            let mine = cookies.filter { url.host?.hasSuffix($0.domain.trimmingCharacters(in: CharacterSet(charactersIn: "."))) ?? false }
            for (name, value) in HTTPCookie.requestHeaderFields(with: mine) {
                request.setValue(value, forHTTPHeaderField: name)
            }
            URLSession.shared.dataTask(with: request) { data, response, error in
                DispatchQueue.main.async {
                    spinner.removeFromSuperview()
                    let http = response as? HTTPURLResponse
                    guard let data = data, error == nil, let status = http?.statusCode, (200..<300).contains(status) else {
                        self.alert("Download failed", error?.localizedDescription ?? "The computer answered \(http?.statusCode ?? 0).")
                        return
                    }
                    let name = self.fileName(url: url, response: http)
                    let file = FileManager.default.temporaryDirectory.appendingPathComponent(name)
                    do {
                        try data.write(to: file, options: .atomic)
                    } catch {
                        self.alert("Download failed", error.localizedDescription)
                        return
                    }
                    let share = UIActivityViewController(activityItems: [file], applicationActivities: nil)
                    share.popoverPresentationController?.sourceView = self.view
                    share.popoverPresentationController?.sourceRect = CGRect(x: self.view.bounds.midX, y: self.view.bounds.midY, width: 1, height: 1)
                    share.popoverPresentationController?.permittedArrowDirections = []
                    self.present(share, animated: true)
                }
            }.resume()
        }
    }

    /// The server's file name (Content-Disposition), else one made from the address.
    private func fileName(url: URL, response: HTTPURLResponse?) -> String {
        if let disposition = response?.value(forHTTPHeaderField: "Content-Disposition"),
           let range = disposition.range(of: "filename=\"") {
            let rest = disposition[range.upperBound...]
            if let end = rest.firstIndex(of: "\"") {
                let name = String(rest[..<end]).replacingOccurrences(of: "/", with: "-")
                if !name.isEmpty { return name }
            }
        }
        let parts = url.pathComponents.filter { $0 != "/" && $0 != "api" && $0 != "pdf" }
        let export = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "export" }?.value
        return (parts.isEmpty ? "document" : parts.joined(separator: "-")) + "." + (export ?? "pdf")
    }

    private func alert(_ title: String, _ message: String) {
        let a = UIAlertController(title: title, message: message, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: "OK", style: .default))
        present(a, animated: true)
    }
}
