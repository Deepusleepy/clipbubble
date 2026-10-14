import AppKit
import CryptoKit
import SQLite3

/// One clipboard entry. For files, `text` holds newline-separated paths.
struct Clip: Identifiable, Hashable {
    let id: Int64
    let kind: String          // "text" | "image" | "file"
    let text: String
    let file: String?         // stored PNG for images
    let source: String        // app name, or "iPhone" for Universal Clipboard
    let created: Date

    var paths: [String] { kind == "file" ? text.split(separator: "\n").map(String.init) : [] }
    var imagePath: String? { kind == "image" ? file : paths.first(where: isImagePath) }
}

let imageExts: Set<String> = ["png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp"]
func isImagePath(_ p: String) -> Bool { imageExts.contains((p as NSString).pathExtension.lowercased()) }

func sha(_ d: Data) -> String { SHA256.hash(data: d).map { String(format: "%02x", $0) }.joined() }
func sha(_ s: String) -> String { sha(Data(s.utf8)) }

enum Paths {
    static let dir = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/ClipBubble")
    static let images = dir.appendingPathComponent("images")
    static let db = dir.appendingPathComponent("clips.sqlite")
}

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// SQLite store shared by the app (writer) and the MCP server (reader).
final class DB {
    static let retention: TimeInterval = 7 * 24 * 3600
    private var h: OpaquePointer?

    init() {
        try? FileManager.default.createDirectory(at: Paths.images, withIntermediateDirectories: true)
        sqlite3_open(Paths.db.path, &h)
        sqlite3_busy_timeout(h, 3000)
        sqlite3_exec(h, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS clips_created ON clips(created);
            """, nil, nil, nil)
    }

    private func prepare(_ sql: String, _ args: [Any?]) -> OpaquePointer? {
        var s: OpaquePointer?
        guard sqlite3_prepare_v2(h, sql, -1, &s, nil) == SQLITE_OK else { return nil }
        for (i, a) in args.enumerated() {
            let idx = Int32(i + 1)
            switch a {
            case let v as String: sqlite3_bind_text(s, idx, v, -1, SQLITE_TRANSIENT)
            case let v as Double: sqlite3_bind_double(s, idx, v)
            case let v as Int64: sqlite3_bind_int64(s, idx, v)
            case let v as Int: sqlite3_bind_int64(s, idx, Int64(v))
            default: sqlite3_bind_null(s, idx)
            }
        }
        return s
    }

    private func run(_ sql: String, _ args: Any?...) {
        let s = prepare(sql, args)
        sqlite3_step(s)
        sqlite3_finalize(s)
    }

    private func select(_ tail: String, _ args: [Any?]) -> [Clip] {
        let s = prepare("SELECT id,kind,text,file,source,created FROM clips " + tail, args)
        defer { sqlite3_finalize(s) }
        func col(_ i: Int32) -> String { sqlite3_column_text(s, i).map { String(cString: $0) } ?? "" }
        var out: [Clip] = []
        while sqlite3_step(s) == SQLITE_ROW {
            out.append(Clip(id: sqlite3_column_int64(s, 0), kind: col(1), text: col(2),
                            file: sqlite3_column_type(s, 3) == SQLITE_NULL ? nil : col(3),
                            source: col(4), created: Date(timeIntervalSince1970: sqlite3_column_double(s, 5))))
        }
        return out
    }

    /// Inserts a clip, or moves an identical existing one to the top.
    func upsert(kind: String, text: String, file: String?, source: String, hash: String) {
        run("""
            INSERT INTO clips(kind,text,file,source,hash,created) VALUES(?,?,?,?,?,?)
            ON CONFLICT(hash) DO UPDATE SET created=excluded.created,
              source=CASE WHEN clips.source = 'Screenshot' THEN clips.source ELSE excluded.source END
            """, kind, text, file, source, hash, Date().timeIntervalSince1970)
    }

    /// Adds a clip with its own timestamp, leaving any existing identical clip untouched.
    func insertIfMissing(kind: String, text: String, source: String, hash: String, created: Date) {
        run("INSERT OR IGNORE INTO clips(kind,text,source,hash,created) VALUES(?,?,?,?,?)",
            kind, text, source, hash, created.timeIntervalSince1970)
    }

    func clips(limit: Int = 300, kind: String? = nil, source: String? = nil,
               query: String? = nil, sinceHours: Double? = nil) -> [Clip] {
        var tail = "WHERE created >= ?"
        var args: [Any?] = [Date().timeIntervalSince1970 - (sinceHours.map { $0 * 3600 } ?? Self.retention)]
        if kind == "image" {
            // Image files (like screenshots) count as images too.
            tail += " AND (kind = 'image' OR (kind = 'file' AND (" +
                imageExts.map { "text LIKE '%.\($0)'" }.joined(separator: " OR ") + ")))"
        } else if let kind {
            tail += " AND kind = ?"
            args.append(kind)
        }
        if let source { tail += " AND source = ? COLLATE NOCASE"; args.append(source) }
        if let query, !query.isEmpty { tail += " AND (text LIKE ? OR source LIKE ?)"; args += ["%\(query)%", "%\(query)%"] }
        tail += " ORDER BY created DESC LIMIT ?"
        args.append(limit)
        return select(tail, args)
    }

    func clip(id: Int64) -> Clip? { select("WHERE id = ?", [id]).first }

    func exists(hash: String) -> Bool { !select("WHERE hash = ?", [hash]).isEmpty }

    func delete(hash: String) {
        removeFiles(select("WHERE hash = ?", [hash]))
        run("DELETE FROM clips WHERE hash = ?", hash)
    }

    private func removeFiles(_ clips: [Clip]) {
        for c in clips { if let f = c.file { try? FileManager.default.removeItem(atPath: f) } }
    }

    func delete(id: Int64) {
        removeFiles(select("WHERE id = ?", [id]))
        run("DELETE FROM clips WHERE id = ?", id)
    }

    func deleteAll() {
        removeFiles(select("", []))
        run("DELETE FROM clips")
    }

    /// Drops everything older than the 7-day retention window.
    func prune() {
        let cutoff = Date().timeIntervalSince1970 - Self.retention
        removeFiles(select("WHERE created < ?", [cutoff]))
        run("DELETE FROM clips WHERE created < ?", cutoff)
    }
}

/// Writes clips back to the macOS clipboard.
enum Board {
    static func pngData(_ path: String) -> Data? {
        if (path as NSString).pathExtension.lowercased() == "png" { return FileManager.default.contents(atPath: path) }
        return NSImage(contentsOfFile: path)?.tiffRepresentation
            .flatMap { NSBitmapImageRep(data: $0) }?.representation(using: .png, properties: [:])
    }

    static func write(_ c: Clip) {
        switch c.kind {
        case "text": writeText(c.text)
        case "image":
            guard let f = c.file, let d = pngData(f) else { return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setData(d, forType: .png)
        default: writeFiles(c.paths.map { URL(fileURLWithPath: $0) })
        }
    }

    static func writeText(_ s: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
    }

    /// A single image file goes on as both the file and its pixels, so it pastes into Finder and into chat/web apps.
    static func writeFiles(_ urls: [URL]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if urls.count == 1, isImagePath(urls[0].path), let d = pngData(urls[0].path) {
            let item = NSPasteboardItem()
            item.setString(urls[0].absoluteString, forType: .fileURL)
            item.setData(d, forType: .png)
            pb.writeObjects([item])
        } else {
            pb.writeObjects(urls as [NSURL])
        }
    }
}
