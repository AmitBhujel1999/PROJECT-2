import UIKit

/// iPhone/iPad app for Mero Lekhapal: asks for the address of the program on the PC, then shows
/// its screens (the same ones as the desktop and Android apps) in a web view.
@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(_ application: UIApplication,
                     didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {
        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = RootViewController()
        window.makeKeyAndVisible()
        self.window = window
        return true
    }
}

/// Saved server address, e.g. "http://192.168.1.84:4173".
enum ServerAddress {
    /// Online copy of the books (Firebase), for phones away from the shop Wi-Fi.
    static let cloud = "https://mero-lekhapal.web.app"

    private static let key = "server"

    static var saved: String? {
        get { UserDefaults.standard.string(forKey: key).flatMap(normalise) }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }

    /// "192.168.1.84:4173" -> "http://192.168.1.84:4173"; nil when it is not a usable address.
    static func normalise(_ text: String) -> String? {
        var s = text.trimmingCharacters(in: .whitespacesAndNewlines)
        while s.hasSuffix("/") { s.removeLast() }
        if s.isEmpty { return nil }
        if !s.lowercased().hasPrefix("http://") && !s.lowercased().hasPrefix("https://") { s = "http://" + s }
        guard let url = URL(string: s), url.host != nil else { return nil }
        return s
    }
}

/// Shows either the address screen or the web screens.
final class RootViewController: UIViewController {
    private var current: UIViewController?

    override var preferredStatusBarStyle: UIStatusBarStyle { .lightContent }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor(named: "Navy")
        if let server = ServerAddress.saved {
            showWeb(server)
        } else {
            showSetup(error: nil)
        }
    }

    func showSetup(error: String?) {
        let setup = SetupViewController(address: ServerAddress.saved, error: error)
        setup.onConnect = { [weak self] address in
            ServerAddress.saved = address
            self?.showWeb(address)
        }
        setup.onOnline = { [weak self] in self?.showWeb(ServerAddress.cloud, start: "/link") }
        show(setup)
    }

    func showWeb(_ server: String, start: String = "/dashboard") {
        let web = WebViewController(server: server, start: start)
        web.onProblem = { [weak self] message in self?.showSetup(error: message) }
        web.onChangeServer = { [weak self] in self?.showSetup(error: nil) }
        show(web)
    }

    private func show(_ child: UIViewController) {
        if let old = current {
            old.willMove(toParent: nil)
            old.view.removeFromSuperview()
            old.removeFromParent()
        }
        addChild(child)
        child.view.frame = view.bounds
        child.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(child.view)
        child.didMove(toParent: self)
        current = child
    }
}
