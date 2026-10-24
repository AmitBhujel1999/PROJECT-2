import UIKit

/// "Connect to your computer": asks for the address shown in the PC program's title bar.
final class SetupViewController: UIViewController, UITextFieldDelegate {
    var onConnect: ((String) -> Void)?
    var onOnline: (() -> Void)?

    private let address: String?
    private let error: String?
    private let field = UITextField()
    private let errorLabel = UILabel()

    init(address: String?, error: String?) {
        self.address = address
        self.error = error
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor(named: "Navy")

        let logo = UIImageView(image: UIImage(named: "Logo"))
        logo.contentMode = .scaleAspectFit
        logo.widthAnchor.constraint(equalToConstant: 64).isActive = true
        logo.heightAnchor.constraint(equalToConstant: 64).isActive = true

        let title = label("Mero Lekhapal", size: 26, weight: .bold, color: .white)
        let tagline = label("ACCOUNTING & INVENTORY", size: 12, weight: .semibold, color: UIColor(white: 1, alpha: 0.5))
        let intro = label("Connect to the Mero Lekhapal program on your computer.", size: 15, weight: .regular,
                          color: UIColor(white: 1, alpha: 0.75))

        field.text = address
        field.placeholder = "http://192.168.1.84:4173"
        field.keyboardType = .URL
        field.autocapitalizationType = .none
        field.autocorrectionType = .no
        field.clearButtonMode = .whileEditing
        field.returnKeyType = .go
        field.borderStyle = .roundedRect
        field.font = .systemFont(ofSize: 17)
        field.delegate = self
        field.heightAnchor.constraint(equalToConstant: 48).isActive = true

        errorLabel.text = error
        errorLabel.isHidden = error == nil
        errorLabel.numberOfLines = 0
        errorLabel.font = .systemFont(ofSize: 14)
        errorLabel.textColor = UIColor(red: 0.6, green: 0.11, blue: 0.11, alpha: 1)
        errorLabel.backgroundColor = UIColor(red: 1, green: 0.93, blue: 0.93, alpha: 1)
        errorLabel.layer.cornerRadius = 8
        errorLabel.clipsToBounds = true
        errorLabel.textAlignment = .center

        let connect = UIButton(type: .system)
        connect.setTitle("Connect", for: .normal)
        connect.titleLabel?.font = .systemFont(ofSize: 17, weight: .semibold)
        connect.setTitleColor(.white, for: .normal)
        connect.backgroundColor = UIColor(red: 0.15, green: 0.35, blue: 0.65, alpha: 1)
        connect.layer.cornerRadius = 10
        connect.heightAnchor.constraint(equalToConstant: 50).isActive = true
        connect.addTarget(self, action: #selector(connectTapped), for: .touchUpInside)

        let help = label(
            "On the computer, open Mero Lekhapal. Its window title shows the phone address, " +
            "e.g. http://192.168.1.84:4173. The iPhone must be on the same Wi-Fi.\n\n" +
            "If iPhone asks to find devices on your local network, tap Allow.",
            size: 13, weight: .regular, color: UIColor(white: 1, alpha: 0.6))

        // Away from the shop: the online copy (the phone must have been linked once on the shop Wi-Fi).
        let online = UIButton(type: .system)
        online.setTitle("Open online copy (away from the shop)", for: .normal)
        online.titleLabel?.font = .systemFont(ofSize: 16, weight: .medium)
        online.setTitleColor(.white, for: .normal)
        online.backgroundColor = UIColor(white: 1, alpha: 0.12)
        online.layer.cornerRadius = 10
        online.heightAnchor.constraint(equalToConstant: 48).isActive = true
        online.addTarget(self, action: #selector(onlineTapped), for: .touchUpInside)
        let onlineHelp = label(
            "Shows the last copy uploaded by the computer, from any Wi-Fi or mobile data. " +
            "Link this phone first: on the shop Wi-Fi open More, then Use this phone anywhere.",
            size: 12, weight: .regular, color: UIColor(white: 1, alpha: 0.55))

        let card = UIStackView(arrangedSubviews: [field, errorLabel, connect, online, onlineHelp])
        card.axis = .vertical
        card.spacing = 12

        let stack = UIStackView(arrangedSubviews: [logo, title, tagline, intro, card, help])
        stack.axis = .vertical
        stack.alignment = .center
        stack.spacing = 10
        stack.setCustomSpacing(16, after: logo)
        stack.setCustomSpacing(18, after: tagline)
        stack.setCustomSpacing(24, after: intro)
        stack.setCustomSpacing(24, after: card)
        stack.translatesAutoresizingMaskIntoConstraints = false
        card.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true

        let scroll = UIScrollView()
        scroll.keyboardDismissMode = .interactive
        scroll.alwaysBounceVertical = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(scroll)
        scroll.addSubview(stack)
        let guide = view.safeAreaLayoutGuide
        // Phones: fill the width less the margins. iPad: at most 440 points wide.
        let fill = stack.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor, constant: -48)
        fill.priority = .defaultHigh
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: guide.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            scroll.leadingAnchor.constraint(equalTo: guide.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: guide.trailingAnchor),
            stack.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor, constant: 48),
            stack.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor, constant: -24),
            stack.centerXAnchor.constraint(equalTo: scroll.frameLayoutGuide.centerXAnchor),
            stack.widthAnchor.constraint(lessThanOrEqualToConstant: 440),
            stack.widthAnchor.constraint(lessThanOrEqualTo: scroll.frameLayoutGuide.widthAnchor, constant: -48),
            fill,
        ])
    }

    private func label(_ text: String, size: CGFloat, weight: UIFont.Weight, color: UIColor) -> UILabel {
        let l = UILabel()
        l.text = text
        l.font = .systemFont(ofSize: size, weight: weight)
        l.textColor = color
        l.numberOfLines = 0
        l.textAlignment = .center
        return l
    }

    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        connectTapped()
        return true
    }

    @objc private func onlineTapped() {
        view.endEditing(true)
        onOnline?()
    }

    @objc private func connectTapped() {
        guard let address = ServerAddress.normalise(field.text ?? "") else {
            errorLabel.text = "Type the address shown on the computer, e.g. http://192.168.1.84:4173"
            errorLabel.isHidden = false
            return
        }
        view.endEditing(true)
        onConnect?(address)
    }
}
