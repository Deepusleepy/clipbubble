import AppKit
import SwiftUI

/// A bubble mascot. "blob" draws the built-in creature in `color`; "emoji" shows emoji; "image" shows files
/// (PNG, GIF, JPG, SVG) from the mascots folder. `hover` and `active` (click/capture) fall back to `idle`.
struct Mascot: Codable, Identifiable, Hashable {
    var id: String
    var name: String
    var kind: String
    var color: String?
    var idle: String?
    var hover: String?
    var active: String?
}

struct MascotError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

/// Mascot storage shared by the app and the MCP server. Changes are broadcast so the running bubble updates live.
enum Mascots {
    static let dir = Paths.dir.appendingPathComponent("mascots")
    static let settings = Paths.dir.appendingPathComponent("settings.json")
    static let changed = Notification.Name("studio.krnl.clipbubble.mascots")

    static let builtins: [Mascot] = [
        Mascot(id: "blob", name: "Blob", kind: "blob", color: "#8E7CFF"),
        Mascot(id: "mint", name: "Mint", kind: "blob", color: "#34D399"),
        Mascot(id: "peach", name: "Peach", kind: "blob", color: "#FF9466"),
        Mascot(id: "sky", name: "Sky", kind: "blob", color: "#4FC3F7"),
        Mascot(id: "cat", name: "Cat", kind: "emoji", idle: "🐱", hover: "😺", active: "😸"),
        Mascot(id: "frog", name: "Frog", kind: "emoji", idle: "🐸"),
        Mascot(id: "ghost", name: "Ghost", kind: "emoji", idle: "👻"),
        Mascot(id: "robot", name: "Robot", kind: "emoji", idle: "🤖"),
        Mascot(id: "classic", name: "Classic icon", kind: "icon"),
    ]

    static func custom() -> [Mascot] {
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }
            .compactMap { try? JSONDecoder().decode(Mascot.self, from: Data(contentsOf: $0)) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func all() -> [Mascot] { builtins + custom() }

    static func activeID() -> String {
        let json = (try? Data(contentsOf: settings)).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        return json?["mascot"] as? String ?? "blob"
    }

    static func active() -> Mascot { all().first { $0.id == activeID() } ?? builtins[0] }

    static func setActive(_ id: String) {
        if let d = try? JSONSerialization.data(withJSONObject: ["mascot": id]) { try? d.write(to: settings) }
        notify()
    }

    static func notify() {
        DistributedNotificationCenter.default().postNotificationName(changed, object: nil, userInfo: nil, deliverImmediately: true)
    }

    static func url(_ file: String) -> URL {
        file.hasPrefix("/") ? URL(fileURLWithPath: file) : dir.appendingPathComponent(file)
    }

    /// Saves a custom mascot. For images, each state is a file path or inline SVG markup; files are copied in.
    @discardableResult
    static func add(name: String, kind: String, color: String? = nil, states: [String: String]) throws -> Mascot {
        guard ["blob", "emoji", "image"].contains(kind) else { throw MascotError("kind must be blob, emoji or image") }
        if kind == "blob", hexColor(color ?? "") == nil { throw MascotError("blob needs color as a hex like #FF6AD5") }
        if kind != "blob", (states["idle"] ?? "").isEmpty { throw MascotError("\(kind) needs an idle value") }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let slug = name.lowercased().map { $0.isLetter || $0.isNumber ? $0 : "-" }
            .reduce(into: "") { s, c in if !(c == "-" && s.hasSuffix("-")) { s.append(c) } }
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        let base = slug.isEmpty ? "mascot" : slug
        var id = base, n = 2
        while all().contains(where: { $0.id == id }) { id = "\(base)-\(n)"; n += 1 }

        var m = Mascot(id: id, name: name, kind: kind, color: kind == "blob" ? color : nil)
        var copied: [URL] = []
        // A failed add leaves nothing behind.
        func fail(_ message: String) -> MascotError {
            copied.forEach { try? FileManager.default.removeItem(at: $0) }
            return MascotError(message)
        }
        for state in ["idle", "hover", "active"] {
            guard var value = states[state]?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { continue }
            if kind == "image" {
                let file: String
                if value.hasPrefix("<svg") || value.hasPrefix("<?xml") {
                    file = "\(id)-\(state).svg"
                    do { try Data(value.utf8).write(to: dir.appendingPathComponent(file)) }
                    catch { throw fail("couldn't save the \(state) SVG: \(error.localizedDescription)") }
                } else {
                    let src = URL(fileURLWithPath: (value as NSString).expandingTildeInPath)
                    file = "\(id)-\(state).\(src.pathExtension.lowercased())"
                    try? FileManager.default.removeItem(at: dir.appendingPathComponent(file))
                    do { try FileManager.default.copyItem(at: src, to: dir.appendingPathComponent(file)) }
                    catch { throw fail("couldn't read the \(state) image at \(src.path)") }
                }
                copied.append(dir.appendingPathComponent(file))
                guard NSImage(contentsOf: dir.appendingPathComponent(file)) != nil else {
                    throw fail("couldn't read the \(state) image; use PNG, GIF, JPG or SVG")
                }
                value = file
            }
            switch state {
            case "idle": m.idle = value
            case "hover": m.hover = value
            default: m.active = value
            }
        }
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        do { try enc.encode(m).write(to: dir.appendingPathComponent(id + ".json")) }
        catch { throw fail("couldn't save the mascot: \(error.localizedDescription)") }
        notify()
        return m
    }

    /// Deletes a custom mascot and its images. Built-ins can't be removed.
    static func remove(_ id: String) -> Bool {
        guard let m = custom().first(where: { $0.id == id }) else { return false }
        // Only this mascot's own files: removing "fox" must not touch "fox-2".
        var files = [id + ".json"]
        if m.kind == "image" { files += [m.idle, m.hover, m.active].compactMap { $0 }.filter { !$0.hasPrefix("/") } }
        for f in Set(files) {
            try? FileManager.default.removeItem(at: dir.appendingPathComponent((f as NSString).lastPathComponent))
        }
        if activeID() == id { setActive("blob") } else { notify() }
        return true
    }
}

// MARK: - Views

/// The mascot inside the bubble. Everything is a cheap transform; nothing animates while idle except a blink.
struct MascotView: View {
    let mascot: Mascot
    let hover: Bool
    let pressed: Bool
    let happy: Bool
    let hoverTicks: Int
    let happyTicks: Int

    private var mood: String { happy || pressed ? "active" : hover ? "hover" : "idle" }

    var body: some View {
        face
            .scaleEffect(x: pressed ? 1.14 : 1, y: pressed ? 0.84 : 1, anchor: .bottom)
            .animation(.spring(response: 0.2, dampingFraction: 0.45), value: pressed)
            .keyframeAnimator(initialValue: 0.0, trigger: hoverTicks) { v, angle in
                v.rotationEffect(.degrees(angle))
            } keyframes: { _ in
                KeyframeTrack {
                    CubicKeyframe(-13, duration: 0.09)
                    CubicKeyframe(10, duration: 0.12)
                    CubicKeyframe(-5, duration: 0.1)
                    CubicKeyframe(0, duration: 0.12)
                }
            }
            .keyframeAnimator(initialValue: 0.0, trigger: happyTicks) { v, y in
                v.offset(y: y)
            } keyframes: { _ in
                KeyframeTrack {
                    SpringKeyframe(-9, duration: 0.14, spring: .snappy)
                    SpringKeyframe(0, duration: 0.18, spring: .bouncy)
                    SpringKeyframe(-3, duration: 0.1)
                    SpringKeyframe(0, duration: 0.14)
                }
            }
    }

    private func value(_ m: Mascot) -> String? {
        switch mood {
        case "active": return m.active ?? m.hover ?? m.idle
        case "hover": return m.hover ?? m.idle
        default: return m.idle
        }
    }

    @ViewBuilder private var face: some View {
        switch mascot.kind {
        case "blob":
            BlobFace(color: hexColor(mascot.color ?? "") ?? Color(red: 0.56, green: 0.49, blue: 1), mood: mood)
        case "emoji":
            Text(value(mascot) ?? "🙂").font(.system(size: 25))
        case "image":
            if let f = value(mascot) {
                AnimatedImage(url: Mascots.url(f), playing: hover || pressed || happy).frame(width: 34, height: 34)
            }
        default:
            Image(systemName: happy ? "checkmark" : "doc.on.clipboard.fill")
                .font(.system(size: 17, weight: .semibold)).foregroundStyle(.white.opacity(0.92))
                .contentTransition(.symbolEffect(.replace))
        }
    }
}

/// The built-in creature: a glossy blob that blinks, perks up on hover and grins when it catches a copy.
struct BlobFace: View {
    let color: Color
    let mood: String
    @State private var blink = false

    var body: some View {
        ZStack {
            Ellipse().fill(color).frame(width: 33, height: 30)
                .overlay(Ellipse().fill(LinearGradient(colors: [.white.opacity(0.45), .clear], startPoint: .top, endPoint: .center)))
                .overlay(Ellipse().fill(LinearGradient(colors: [.clear, .black.opacity(0.18)], startPoint: .center, endPoint: .bottom)))
            HStack(spacing: 7) { eye; eye }.offset(y: -2.5)
            HStack(spacing: 16) {
                Circle().fill(Color(red: 1, green: 0.45, blue: 0.6).opacity(0.55)).frame(width: 4.5, height: 3.5)
                Circle().fill(Color(red: 1, green: 0.45, blue: 0.6).opacity(0.55)).frame(width: 4.5, height: 3.5)
            }
            .offset(y: 3.5)
            mouth.offset(y: 6.5)
        }
        .animation(.spring(response: 0.18, dampingFraction: 0.7), value: mood)
        .task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(Double.random(in: 2.5...5.5)))
                blink = true
                try? await Task.sleep(for: .milliseconds(110))
                blink = false
            }
        }
    }

    @ViewBuilder private var eye: some View {
        if mood == "active" {
            Arc(up: true).stroke(Color.black.opacity(0.8), style: StrokeStyle(lineWidth: 1.8, lineCap: .round))
                .frame(width: 6, height: 3.5)
        } else {
            ZStack {
                Capsule().fill(.white).frame(width: 6.5, height: mood == "hover" ? 9.5 : 8.5)
                Circle().fill(Color.black.opacity(0.85)).frame(width: 4, height: 4)
                    .offset(y: mood == "hover" ? -1 : 1)
                Circle().fill(.white).frame(width: 1.4, height: 1.4).offset(x: 0.8, y: mood == "hover" ? -1.8 : 0.2)
            }
            .scaleEffect(y: blink ? 0.12 : 1)
        }
    }

    @ViewBuilder private var mouth: some View {
        switch mood {
        case "active":
            Arc(up: false, closed: true).fill(Color.black.opacity(0.75)).frame(width: 8, height: 4.5)
        case "hover":
            Ellipse().fill(Color.black.opacity(0.7)).frame(width: 3.5, height: 4)
        default:
            Arc(up: false).stroke(Color.black.opacity(0.7), style: StrokeStyle(lineWidth: 1.5, lineCap: .round))
                .frame(width: 6, height: 2.5)
        }
    }
}

/// A shallow curve: `up` is a "^" (happy eye); otherwise a smile, optionally closed into an open mouth.
struct Arc: Shape {
    var up: Bool
    var closed = false

    func path(in r: CGRect) -> Path {
        var p = Path()
        let y0 = up ? r.maxY : r.minY
        let ctrl = up ? r.minY - r.height : r.maxY + r.height
        p.move(to: CGPoint(x: r.minX, y: y0))
        p.addQuadCurve(to: CGPoint(x: r.maxX, y: y0), control: CGPoint(x: r.midX, y: ctrl))
        if closed { p.closeSubpath() }
        return p
    }
}

/// NSImageView so GIFs can play; they only animate while the mascot is being interacted with.
struct AnimatedImage: NSViewRepresentable {
    let url: URL
    let playing: Bool
    private static var cache: [URL: NSImage] = [:]

    static func clearCache() { cache.removeAll() }

    func makeNSView(context: Context) -> NSImageView {
        let v = NSImageView()
        v.imageScaling = .scaleProportionallyUpOrDown
        v.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        v.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        return v
    }

    func updateNSView(_ v: NSImageView, context: Context) {
        let img = Self.cache[url] ?? NSImage(contentsOf: url)
        Self.cache[url] = img
        if v.image !== img { v.image = img }
        v.animates = playing
    }
}
