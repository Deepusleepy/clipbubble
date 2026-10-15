using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipBubble;

enum ClipKind { Text, Image, File }

/// One clipboard entry. For files, `Text` holds newline-separated paths.
sealed record Clip(long Id, ClipKind Kind, string Text, string? File, string Source, DateTime Created)
{
    public string[] Paths => Kind == ClipKind.File ? Text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    public string? ImagePath => Kind == ClipKind.Image ? File : Paths.FirstOrDefault(Store.IsImagePath);
}

static class Store
{
    public static readonly string[] ImageExts = { "png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp" };
    public static bool IsImagePath(string p) => ImageExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant());

    public static string Sha(byte[] d)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(d).Select(b => b.ToString("x2")));
    }
    public static string Sha(string s) => Sha(Encoding.UTF8.GetBytes(s));

    public static string Name(this ClipKind k) => k.ToString().ToLowerInvariant();
    public static ClipKind? ParseKind(string? s) => s switch { "text" => ClipKind.Text, "image" => ClipKind.Image, "file" => ClipKind.File, _ => null };

    /// Writes through a temp file so a reader never sees half a file. Retries briefly if another process
    /// (the MCP server, or a second write) has the file open at that instant.
    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        System.IO.File.WriteAllBytes(tmp, data);
        for (var i = 0; ; i++)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
                else System.IO.File.Move(tmp, path);
                return;
            }
            catch (IOException) when (i < 10) { Thread.Sleep(20); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { System.IO.File.Delete(tmp); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }   // the original error is the one to report
                throw;
            }
        }
    }

    /// Reads without blocking a writer from replacing the file at the same moment.
    public static string ReadText(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// What reading a damaged, unsupported or unreadable picture throws. WIC errors that .NET doesn't map
    /// (a broken WebP, for one) arrive as COMException.
    public static bool Unreadable(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or COMException or OutOfMemoryException;

    public static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

    /// Same pixels, whatever the encoding: a screenshot tool's clipboard copy and the file it saved.
    public static bool SamePicture(Func<Stream> a, Func<Stream> b)
    {
        try
        {
            static (int, int) Size(Func<Stream> open)
            {
                using var s = open();
                var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                return (frame.PixelWidth, frame.PixelHeight);
            }
            static byte[] Pixels(Func<Stream> open)
            {
                using var s = open();
                var frame = new FormatConvertedBitmap(BitmapDecoder.Create(s, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0],
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                frame.CopyPixels(px, frame.PixelWidth * 4, 0);
                return px;
            }
            // Comparing sizes reads only the headers; the full decode runs only for a likely match.
            return Size(a) == Size(b) && Pixels(a).SequenceEqual(Pixels(b));
        }
        catch (Exception e) when (Unreadable(e)) { return false; }
    }

    /// Waits (up to 2 s) while another app still has the file open for writing, like a screenshot tool saving it.
    /// A half-written picture never matches its copy.
    public static void WaitUntilWritten(string path)
    {
        for (var i = 0; i < 20; i++)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return; }
            catch (IOException) when (System.IO.File.Exists(path)) { Thread.Sleep(100); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        }
    }

    /// The first `n` characters, without cutting an emoji or other surrogate pair in half.
    public static string Head(string s, int n) => s.Length <= n ? s : s.Substring(0, char.IsHighSurrogate(s[n - 1]) ? n - 1 : n);

    public static Stream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// Decodes at most `px` on the longer side, so a huge picture shown small doesn't hold its full size in memory.
    /// Also returns the full size.
    public static (BitmapSource Image, Size Full) DecodeFit(string path, int px)
    {
        using var file = OpenShared(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        file.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = file;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(px, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(px, frame.PixelHeight);
        image.EndInit();
        image.Freeze();
        return (image, new Size(frame.PixelWidth, frame.PixelHeight));
    }

    /// Decodes through a stream that's closed right after. Decoding straight from a path leaves the file locked
    /// until the next garbage collection when it fails, so a broken picture couldn't be deleted or renamed.
    public static BitmapDecoder Decode(string path, BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile)
    {
        using var s = OpenShared(path);
        return BitmapDecoder.Create(s, options, BitmapCacheOption.OnLoad);
    }

    public static byte[] Png(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(image as BitmapFrame ?? BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// Unexpected errors go to errors.log in the data folder instead of vanishing (or closing the app).
static class Log
{
    public static void Error(Exception e)
    {
        try
        {
            var path = Path.Combine(Paths.Dir, "errors.log");
            if (System.IO.File.Exists(path) && new FileInfo(path).Length > 1_000_000) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}");
        }
        catch (Exception) { }   // logging must never be the thing that fails
    }
}

static class Paths
{
    /// `CLIPBUBBLE_HOME` moves everything (tests use it; it also makes a portable copy possible).
    public static readonly string Dir = Environment.GetEnvironmentVariable("CLIPBUBBLE_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBubble");
    public static readonly string Images = Path.Combine(Dir, "images");
    public static readonly string Db = Path.Combine(Dir, "clips.sqlite");
}

/// SQLite store shared by the app (writer) and the MCP server (reader). Uses the SQLite that ships with Windows.
sealed class Db : IDisposable
{
    public static readonly double Retention = 7 * 24 * 3600;
    readonly IntPtr h;

    public Db()
    {
        Directory.CreateDirectory(Paths.Images);
        if (sqlite3_open16(Paths.Db, out h) != 0) throw new IOException("Can't open " + Paths.Db);
        sqlite3_busy_timeout(h, 3000);
        var rc = sqlite3_exec(h, Encoding.UTF8.GetBytes(@"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS clips_created ON clips(created);
            CREATE TABLE IF NOT EXISTS forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL);" + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc != 0) throw new IOException($"Can't set up {Paths.Db}: " + Marshal.PtrToStringUni(sqlite3_errmsg16(h)));
    }

    public void Dispose() => sqlite3_close_v2(h);

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    IntPtr Prepare(string sql, object?[] args)
    {
        if (sqlite3_prepare16_v2(h, sql, -1, out var s, IntPtr.Zero) != 0) throw new InvalidOperationException(Marshal.PtrToStringUni(sqlite3_errmsg16(h)) + "\n" + sql);
        for (var i = 0; i < args.Length; i++)
        {
            var idx = i + 1;
            switch (args[i])
            {
                case string v: sqlite3_bind_text16(s, idx, v, v.Length * 2, Transient); break;   // explicit length keeps text after a NUL
                case double v: sqlite3_bind_double(s, idx, v); break;
                case long v: sqlite3_bind_int64(s, idx, v); break;
                case int v: sqlite3_bind_int64(s, idx, v); break;
                default: sqlite3_bind_null(s, idx); break;
            }
        }
        return s;
    }

    /// Throws when SQLite reports an error (a database still locked after the 3 s busy timeout, a full disk),
    /// so a failed write is never mistaken for a saved clip.
    void Run(string sql, params object?[] args)
    {
        var s = Prepare(sql, args);
        try { Check(sqlite3_step(s)); }
        finally { sqlite3_finalize(s); }
    }

    void Check(int rc)
    {
        if (rc != SQLITE_ROW && rc != SQLITE_DONE) throw new InvalidOperationException("SQLite: " + Marshal.PtrToStringUni(sqlite3_errmsg16(h)));
    }

    List<Clip> Select(string tail, params object?[] args)
    {
        var s = Prepare("SELECT id,kind,text,file,source,created FROM clips " + tail, args);
        try
        {
            string Col(int i) => sqlite3_column_text16(s, i) is var p && p != IntPtr.Zero ? Marshal.PtrToStringUni(p, sqlite3_column_bytes16(s, i) / 2) : "";
            var output = new List<Clip>();
            int rc;
            while ((rc = sqlite3_step(s)) == SQLITE_ROW)
            {
                if (Store.ParseKind(Col(1)) is not { } kind) continue;
                output.Add(new Clip(sqlite3_column_int64(s, 0), kind, Col(2),
                    sqlite3_column_type(s, 3) == SQLITE_NULL ? null : Col(3), Col(4),
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(sqlite3_column_double(s, 5) * 1000)).LocalDateTime));
            }
            Check(rc);
            return output;
        }
        finally { sqlite3_finalize(s); }
    }

    /// Inserts a clip, or moves an identical existing one to the top. `keepSource` is for ClipBubble's own writes
    /// (a picked clip, an agent's clipboard_set): a known clip keeps its source, a new one gets `source`.
    public void Upsert(ClipKind kind, string text, string? file, string source, string hash, bool keepSource = false) =>
        Run(@"INSERT INTO clips(kind,text,file,source,hash,created) VALUES(?,?,?,?,?,?)
              ON CONFLICT(hash) DO UPDATE SET created=excluded.created,
                source=CASE WHEN clips.source IN ('Screenshot', 'Downloads') OR ?7 THEN clips.source ELSE excluded.source END",
            kind.Name(), text, file, source, hash, Now, keepSource ? 1 : 0);

    /// Adds a clip with its own timestamp, leaving any existing identical clip untouched, unless the user deleted it
    /// (a screenshot or download they removed stays removed, like on the Mac).
    public void InsertIfMissing(ClipKind kind, string text, string source, string hash, DateTime created) =>
        Run("INSERT OR IGNORE INTO clips(kind,text,source,hash,created) SELECT ?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM forgotten WHERE hash = ?)",
            kind.Name(), text, source, hash, new DateTimeOffset(created).ToUnixTimeMilliseconds() / 1000.0, hash);

    public List<Clip> Clips(int limit = 300, ClipKind? kind = null, string? source = null, string? query = null, double? sinceHours = null)
    {
        var tail = "WHERE created >= ?";
        var args = new List<object?> { Now - (sinceHours.HasValue ? sinceHours.Value * 3600 : Retention) };
        if (kind == ClipKind.Image)
        {
            // Image files (like screenshots) count as images too.
            tail += " AND (kind = 'image' OR (kind = 'file' AND (" + string.Join(" OR ", Store.ImageExts.Select(e => $"text LIKE '%.{e}'")) + ")))";
        }
        else if (kind is { } k)
        {
            tail += " AND kind = ?";
            args.Add(k.Name());
        }
        if (source != null) { tail += " AND source = ? COLLATE NOCASE"; args.Add(source); }
        if (query is { Length: > 0 })
        {
            // Match % and _ literally: search is plain text, not a pattern.
            var q = "%" + query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            tail += @" AND (text LIKE ? ESCAPE '\' OR source LIKE ? ESCAPE '\')";
            args.Add(q);
            args.Add(q);
        }
        tail += Prefs.HiddenSql;
        tail += " ORDER BY created DESC LIMIT ?";
        args.Add(limit);
        return Select(tail, args.ToArray());
    }

    /// Switched-off categories stay hidden here too, so an agent can't fetch them by id.
    public Clip? Clip(long id) => Select("WHERE id = ?" + Prefs.HiddenSql, id).FirstOrDefault();

    public bool Exists(string hash) => Select("WHERE hash = ?", hash).Count > 0;

    // Win+PrtScn and Snipping Tool both copy a screenshot and save it. The saved file's row stands for both,
    // whichever of the two ClipBubble sees first (the Mac never does both, so this is Windows-only).
    const double ScreenshotWindow = 10;

    /// A screenshot file just arrived: drop the copy of the same picture recorded moments ago.
    public void DropCopiesOf(string screenshot)
    {
        var copies = Select("WHERE kind = 'image' AND created >= ?", Now - ScreenshotWindow);
        if (copies.Count == 0 || !Store.IsImagePath(screenshot)) return;   // a screen recording is never a copied picture
        Store.WaitUntilWritten(screenshot);
        foreach (var c in copies)
            if (c.File is { } f && Store.SamePicture(() => Store.OpenShared(f), () => Store.OpenShared(screenshot))) Delete(c.Id);
    }

    /// A copied picture is a screenshot that was saved moments ago.
    public bool IsRecentScreenshot(byte[] png) =>
        Select("WHERE source = 'Screenshot' AND created >= ?", Now - ScreenshotWindow).Where(c => Store.IsImagePath(c.Text))
            .Any(c => { Store.WaitUntilWritten(c.Text); return Store.SamePicture(() => new MemoryStream(png), () => Store.OpenShared(c.Text)); });

    /// Removes a clip ClipBubble captured itself (a paste-through), without remembering it as deleted.
    public void Delete(string hash) => DeleteWhere("WHERE hash = ?", false, hash);

    /// The user deleted these: screenshots and downloads among them are remembered in `forgotten`, so the folder
    /// import doesn't bring them back.
    public void Delete(long id) => DeleteWhere("WHERE id = ?", true, id);
    public void DeleteAll() => DeleteWhere("", true);

    /// Drops everything older than the 7-day retention window. A file older than that is never imported again, so
    /// its reminder can go a day after.
    public void Prune()
    {
        DeleteWhere("WHERE created < ?", false, Now - Retention);
        Run("DELETE FROM forgotten WHERE at < ?", Now - Retention - 86400);
    }

    /// Rows go first and files after, so a delete that fails (a locked database) never leaves a row without its image.
    /// Remembering and deleting happen together or not at all.
    void DeleteWhere(string where, bool remember, params object?[] args)
    {
        var gone = Select(where, args);
        Run("BEGIN IMMEDIATE");
        try
        {
            if (remember)
                foreach (var c in gone.Where(c => c.Source is "Screenshot" or "Downloads"))
                    Run("INSERT OR REPLACE INTO forgotten(hash, at) SELECT hash, ? FROM clips WHERE id = ?", Now, c.Id);
            Run("DELETE FROM clips " + where, args);
            Run("COMMIT");
        }
        catch
        {
            try { Run("ROLLBACK"); } catch (InvalidOperationException) { }   // the original error is the one to report
            throw;
        }
        RemoveFiles(gone);
    }

    /// ClipBubble 1.3.0 for Windows kept removed screenshots and downloads in forgotten.json. They move into
    /// `forgotten` once, with when they were removed: each path it listed, and everything in `folders` (the screenshot
    /// and Downloads folders, watched or not) added before its last Clear all. False while the file, or one of those
    /// folders, can't be read, so the folder import waits rather than bringing those files back.
    public bool ImportLegacyForgotten(IEnumerable<string?> folders)
    {
        var json = Path.Combine(Paths.Dir, "forgotten.json");
        if (!System.IO.File.Exists(json)) return true;
        Dictionary<string, object?> old;
        try { old = Store.Json.Deserialize<Dictionary<string, object?>>(Store.ReadText(json)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { old = new(); }   // not JSON: nothing to keep
        static double Seconds(object? ms)
        {
            try { return ms == null ? 0 : Convert.ToDouble(ms, System.Globalization.CultureInfo.InvariantCulture) / 1000; }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return 0; }
        }
        var clearedAt = Seconds(old.GetValueOrDefault("ClearedAt"));
        static double Added(string path) => new DateTimeOffset(System.IO.File.GetCreationTime(path)).ToUnixTimeMilliseconds() / 1000.0;
        // 1.3.0 let a file saved at a removed path later come back in; one already there now still does.
        var removed = (old.GetValueOrDefault("Files") as Dictionary<string, object?> ?? new())
            .Select(p => (Path: p.Key, At: Seconds(p.Value) is > 0 and var at ? at : Now))
            .Where(p => !System.IO.File.Exists(p.Path) || Added(p.Path) <= p.At)
            .ToDictionary(p => p.Path, p => p.At);
        // A folder that isn't there (an unplugged drive) keeps the file around, so the Clear all still covers it once it
        // is, until that Clear all is older than anything the import looks at.
        var missing = false;
        if (clearedAt > 0)
            foreach (var folder in folders.Where(f => f != null))
            {
                if (!Directory.Exists(folder)) { missing = true; continue; }
                try
                {
                    foreach (var entry in new DirectoryInfo(folder!).EnumerateFileSystemInfos())
                        if (new DateTimeOffset(entry.CreationTime).ToUnixTimeMilliseconds() / 1000.0 <= clearedAt && !removed.ContainsKey(entry.FullName))
                            removed[entry.FullName] = clearedAt;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            }
        foreach (var r in removed) Run("INSERT OR REPLACE INTO forgotten(hash, at) VALUES(?, ?)", "f:" + Store.Sha(r.Key), r.Value);
        if (!missing || clearedAt < Now - Retention) System.IO.File.Delete(json);
        return true;
    }

    /// Only our own copies of images are deleted, never a screenshot or download the clip points at.
    static void RemoveFiles(IEnumerable<Clip> clips)
    {
        foreach (var c in clips)
            if (c.File is { } f && Path.GetDirectoryName(f) == Paths.Images) try { System.IO.File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    const int SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_NULL = 5;
    static readonly IntPtr Transient = new(-1);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, IntPtr err);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_bind_text16(IntPtr s, int i, string v, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_double(IntPtr s, int i, double v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr s, int i, long v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_null(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern double sqlite3_column_double(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_type(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes16(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_errmsg16(IntPtr db);
}

/// What gets saved, plus the app's other settings (the Mac keeps these in UserDefaults).
/// Off means a category isn't captured and existing items are hidden everywhere, MCP included.
static class Prefs
{
    public sealed record Category(string Key, string Title, string Icon, string? Detail, string Sql);

    /// Windows has no Universal Clipboard, so the Mac's iPhone category has no switch here.
    public static readonly Category[] Categories =
    {
        new("text", "Copied text", "\uE8E4", null, "kind = 'text' AND source <> 'iPhone'"),
        new("images", "Copied images", "\uEB9F", null, "kind = 'image' AND source <> 'iPhone'"),
        new("files", "Copied files", "\uE8A5", "Files you copy in File Explorer", "kind = 'file' AND source NOT IN ('Screenshot', 'Downloads', 'iPhone')"),
        new("screenshots", "Screenshots", "\uE722", "Every new screenshot, even ones you don't copy", "source = 'Screenshot'"),
        new("downloads", "Recent downloads", "\uE896", "New files in your Downloads folder", "source = 'Downloads'"),
    };

    static readonly string File = Path.Combine(Paths.Dir, "prefs.json");
    static volatile Dictionary<string, object?> values = Load() ?? new();   // swapped whole; read from the writer thread too

    /// Null when the file can't be read right now; a missing file means defaults.
    static Dictionary<string, object?>? Load()
    {
        if (!System.IO.File.Exists(File)) return new();
        try { return Store.Json.Deserialize<Dictionary<string, object?>>(Store.ReadText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    /// The MCP server calls this before each query so it sees switches flipped in the running app. If the file
    /// can't be read, the last known switches stay, so a hidden category never briefly reappears.
    public static void Reload() => values = Load() ?? values;

    public static T Get<T>(string key, T fallback) => values.TryGetValue(key, out var v) && v is T t ? t : fallback;

    /// Copy, save, then swap: the capture thread may be reading `values`, and memory only changes once the file has.
    public static void Set(string key, object? value)
    {
        var next = new Dictionary<string, object?>(values);
        if (value == null) next.Remove(key); else next[key] = value;
        Directory.CreateDirectory(Paths.Dir);
        Store.WriteAtomic(File, Encoding.UTF8.GetBytes(Store.Json.Serialize(next)));
        values = next;
    }

    public static bool On(string key) => Get("save." + key, true);
    public static void SetOn(string key, bool on) => Set("save." + key, on);
    public static string HiddenSql => string.Concat(Categories.Where(c => !On(c.Key)).Select(c => $" AND NOT ({c.Sql})"));
}

/// Writes clips back to the Windows clipboard.
static class Board
{
    /// Password managers and clipboard tools mark copies that history apps must skip.
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    public static byte[]? PngData(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return System.IO.File.ReadAllBytes(path);
            return Store.Png(Store.Decode(path, BitmapCreateOptions.None).Frames[0]);
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }

    /// Null once it's on the clipboard, otherwise why it isn't. Pasting then would paste whatever was there before.
    /// Of several files, only the ones still there go on.
    public static string? Write(Clip c)
    {
        var files = c.Paths.Where(p => System.IO.File.Exists(p) || Directory.Exists(p)).ToList();
        var data = c.Kind switch
        {
            ClipKind.Text => TextData(c.Text),
            ClipKind.Image => c.File is { } f && PngData(f) is { } png ? ImageData(png) : null,
            _ => files.Count > 0 ? FilesData(files) : null,
        };
        if (data == null) return c.Kind == ClipKind.Image ? "Its image file is gone or can't be read." : "Its files are gone.";
        try { Set(data); return null; }
        catch (ExternalException) { return "Another app is using the clipboard. Try again in a moment."; }   // past WPF's own retries
    }

    /// `transient` marks it so clipboard managers (ClipBubble included) and Windows' own history don't save it.
    public static void WriteText(string s, bool transient = false) => Set(TextData(s, transient));

    public static void WriteFiles(IReadOnlyList<string> paths) => Set(FilesData(paths));

    static DataObject TextData(string s, bool transient = false)
    {
        var data = new DataObject();
        data.SetText(s, TextDataFormat.UnicodeText);
        if (transient)
        {
            data.SetData(ExcludeFormat, new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        }
        return data;
    }

    /// A single image file goes on as both the file and its pixels, so it pastes into Explorer and into chat/web apps.
    static DataObject FilesData(IReadOnlyList<string> paths)
    {
        var data = (paths.Count == 1 && Store.IsImagePath(paths[0]) && PngData(paths[0]) is { } png ? ImageData(png) : null) ?? new DataObject();
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        data.SetFileDropList(list);
        return data;
    }

    /// PNG keeps transparency for apps that read it; the bitmap is for everything else. Null when the pixels can't
    /// be read (a file still being written).
    static DataObject? ImageData(byte[] png)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(png));
            data.SetImage(frame);
            return data;
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }

    /// WPF already retries for about a second if another app holds the clipboard. `copy` keeps the data after we exit.
    static void Set(DataObject data) => Clipboard.SetDataObject(data, true);
}
