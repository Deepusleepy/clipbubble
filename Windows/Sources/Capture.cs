using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipBubble;

/// What was on the clipboard, copied out while it was open. Decoding and hashing happen later, off the UI thread.
sealed record Snapshot(string[] Files, string? Text, byte[]? Png, byte[]? Dib);

/// Listens for clipboard changes and records each one once it has settled.
sealed class Capture : IDisposable
{
    readonly HwndSource listener;
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromSeconds(0.6) };
    readonly Recorder recorder = new();
    readonly Action onChange;
    Task chain = Task.CompletedTask;
    uint seen, handled;
    (string Name, bool Ours) source = ("", false);
    int busyRetries;

    public Capture(Action onChange)
    {
        this.onChange = onChange;
        handled = seen = GetClipboardSequenceNumber();
        listener = new HwndSource(new HwndSourceParameters("ClipBubble clipboard") { ParentWindow = new IntPtr(-3) });   // message-only
        listener.AddHook(Hook);
        AddClipboardFormatListener(listener.Handle);
        settle.Tick += (_, _) => Settled();
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(listener.Handle);
        listener.Dispose();
        settle.Stop();
    }

    // Let the clipboard settle first. Paste-through tools like Wispr Flow put text on it, press Ctrl+V and then
    // restore the old contents; reading during that window could make them paste the wrong thing.
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handledMsg)
    {
        if (msg != 0x031D) return IntPtr.Zero;   // WM_CLIPBOARDUPDATE
        seen = GetClipboardSequenceNumber();
        source = Apps.ClipboardSource();   // who copied it; by the time it settles another app may be in front
        busyRetries = 0;
        settle.Stop();
        settle.Start();
        return IntPtr.Zero;
    }

    void Settled()
    {
        settle.Stop();
        if (seen == handled) return;
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); }
        catch (COMException) when (busyRetries++ < 2) { settle.Start(); return; }   // another app has it open (each try waits ~1 s)
        catch (COMException) { handled = seen; return; }
        handled = seen;
        if (data == null || Read(data) is not { } snapshot) return;
        var from = source;
        chain = chain.ContinueWith(_ =>
        {
            try { if (recorder.Record(snapshot, from.Name, DateTime.Now, from.Ours)) Application.Current?.Dispatcher.BeginInvoke(onChange); }
            catch (Exception e) { Log.Error(e); }   // e.g. the database stayed locked: this copy is lost, the next one isn't
        }, TaskScheduler.Default);
    }

    /// Copies the formats we care about. Copies marked as secret by password managers are never read.
    internal static Snapshot? Read(IDataObject data)
    {
        string[] formats;
        try { formats = data.GetFormats(false); }
        catch (COMException) { return null; }
        if (formats.Length == 0 || formats.Contains(Board.ExcludeFormat) || formats.Contains("Clipboard Viewer Ignore")) return null;
        if (formats.Contains("CanIncludeInClipboardHistory") && Bytes(data, "CanIncludeInClipboardHistory") is { Length: >= 4 } allow && BitConverter.ToInt32(allow, 0) == 0) return null;
        var files = formats.Contains(DataFormats.FileDrop) ? Get(data, DataFormats.FileDrop) as string[] : null;
        var text = formats.Contains(DataFormats.UnicodeText) ? Get(data, DataFormats.UnicodeText) as string : null;
        var png = formats.Contains("PNG") ? Bytes(data, "PNG") : null;
        // Asked for even when unlisted: apps that copy only a Bitmap (all .NET apps) get a DIB made by Windows on request.
        var dib = png == null && (formats.Contains(DataFormats.Dib) || formats.Contains(DataFormats.Bitmap)) ? Bytes(data, DataFormats.Dib) : null;
        return new Snapshot(files ?? Array.Empty<string>(), text, png, dib);
    }

    static object? Get(IDataObject data, string format)
    {
        try { return data.GetData(format); }
        catch (Exception e) when (e is COMException or OutOfMemoryException or InvalidOperationException) { return null; }
    }

    static byte[]? Bytes(IDataObject data, string format) => Get(data, format) is MemoryStream ms ? ms.ToArray() : null;

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

/// Turns settled clipboard snapshots into history rows, and spots tools that paste through the clipboard.
/// Runs on one background thread at a time.
sealed class Recorder
{
    readonly Db db = new();
    /// The latest capture and the one before it, to spot tools that paste through the clipboard.
    (string Hash, DateTime At, bool Inserted)? last;
    string? previousHash;

    /// Returns whether anything changed that the UI should show.
    /// `ours` means ClipBubble itself put it on the clipboard (see Db.Upsert's keepSource).
    public bool Record(Snapshot s, string source, DateTime now, bool ours = false)
    {
        if (Classify(s) is not { } clip) return false;
        if (clip.Hash == last?.Hash) return false;
        var category = clip.Kind switch { ClipKind.Image => "images", ClipKind.File => "files", _ => "text" };
        if (!Prefs.On(category)) return false;

        // The previous contents came straight back, so what we just saw was a paste-through: drop it,
        // and don't bump the restored item.
        if (clip.Hash == previousHash && last is { } l && (now - l.At).TotalSeconds < 2.5)
        {
            if (l.Inserted) db.Delete(l.Hash);
            last = (clip.Hash, now, false);
            previousHash = null;
            return true;
        }

        if (clip.Png is { } shot && db.IsRecentScreenshot(shot)) return false;   // its saved file is already in history

        var inserted = !db.Exists(clip.Hash);
        if (clip.Png is { } png)
        {
            var path = Path.Combine(Paths.Images, clip.Hash.Substring(2) + ".png");
            if (!File.Exists(path)) Store.WriteAtomic(path, png);
            clip = clip with { Text = "", File = path };
        }
        db.Upsert(clip.Kind, clip.Text, clip.File, source, clip.Hash, keepSource: ours);
        previousHash = last?.Hash;
        last = (clip.Hash, now, inserted);
        return true;
    }

    record Classified(ClipKind Kind, string Text, string? File, string Hash, byte[]? Png);

    /// Same order as the Mac: files, then an image unless there's real text alongside it, then text.
    static Classified? Classify(Snapshot s)
    {
        var trimmed = s.Text?.Trim() ?? "";
        var looksLikeUrl = !trimmed.Contains(' ') && !trimmed.Contains('\n') && trimmed.StartsWith("http");
        if (s.Files.Length > 0)
        {
            var paths = string.Join("\n", s.Files);
            return new(ClipKind.File, paths, null, "f:" + Store.Sha(paths), null);
        }
        if ((trimmed.Length == 0 || looksLikeUrl) && (s.Png ?? DibToPng(s.Dib)) is { } png)
            return new(ClipKind.Image, "", null, "i:" + Store.Sha(png), png);
        if (trimmed.Length > 0) return new(ClipKind.Text, s.Text!, null, "t:" + Store.Sha(s.Text!), null);
        return null;
    }

    /// A clipboard DIB is a .bmp file without its 14-byte file header; add one and let WIC decode it.
    static byte[]? DibToPng(byte[]? dib)
    {
        if (dib == null || dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
        var masks = headerSize == 40 && compression == 3 ? 12 : 0;
        var palette = bits <= 8 ? (used != 0 ? used : 1 << bits) * 4 : 0;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(frame);
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException) { return null; }
    }
}

/// Names and icons of the apps things were copied from.
static class Apps
{
    static readonly string OwnExe = Process.GetCurrentProcess().MainModule!.FileName;
    static readonly string File = Path.Combine(Paths.Dir, "apps.json");
    /// Remembered across launches so rows keep their app icons after a restart.
    static readonly Dictionary<string, string> PathsByName = Load();

    static Dictionary<string, string> Load()
    {
        try { return Store.Json.Deserialize<Dictionary<string, string>>(Store.ReadText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return new(); }
    }

    /// The app that put what's on the clipboard there. When that's ClipBubble itself (a picked clip, or its MCP
    /// server for an agent), it's the app in front instead, like the Mac, and `Ours` is set.
    public static (string Name, bool Ours) ClipboardSource()
    {
        var owner = GetClipboardOwner();
        Win.GetWindowThreadProcessId(owner != IntPtr.Zero ? owner : Win.GetForegroundWindow(), out var pid);
        if (!IsOurs((int)pid)) return (Name((int)pid) ?? "Windows", false);
        Win.GetWindowThreadProcessId(Win.GetForegroundWindow(), out var front);
        return (IsOurs((int)front) ? "Windows" : Name((int)front) ?? "Windows", true);
    }

    static bool IsOurs(int pid) => ExePath(pid) is { } exe && string.Equals(exe, OwnExe, StringComparison.OrdinalIgnoreCase);

    public static string? Name(int pid)
    {
        if (pid == 0 || ExePath(pid) is not { } exe) return null;
        string name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() ?? ""; }
        catch (System.IO.FileNotFoundException) { name = ""; }
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exe);
        lock (PathsByName)
        {
            if (PathsByName.TryGetValue(name, out var known) && known == exe) return name;
            PathsByName[name] = exe;
            try { Store.WriteAtomic(File, Encoding.UTF8.GetBytes(Store.Json.Serialize(PathsByName))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return name;
    }

    /// The exe behind a source name seen this session, so rows can show its icon.
    public static string? ExePath(string name) { lock (PathsByName) return PathsByName.TryGetValue(name, out var p) ? p : null; }

    static string? ExePath(int pid)
    {
        var h = Win.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Win.CloseHandle(h); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}

/// Watches a folder (screenshots, Downloads) and hands each new item that passes `include` to `onNew`.
sealed class FolderWatcher : IDisposable
{
    public static readonly HashSet<string> TempExts = new() { "crdownload", "download", "part", "partial", "tmp", "opdownload" };

    /// Where Windows saves screenshots (Win+PrtScn and Snipping Tool's auto-save): Pictures\Screenshots.
    public static string DefaultScreenshotFolder => KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// A folder picked in Settings wins over the default.
    public static string ScreenshotFolder => Prefs.Get<string?>("screenshotFolder", null) is { Length: > 0 } custom ? custom : DefaultScreenshotFolder;

    public static string? DownloadsFolder => KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));

    public static bool CanRead(string? folder)
    {
        if (folder == null) return false;
        try { Directory.EnumerateFileSystemEntries(folder).Any(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public readonly string Folder;
    readonly Func<string, bool> include;
    readonly double? maxAge;
    readonly Action<string, DateTime> onNew;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromSeconds(0.4) };
    readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(5) };
    HashSet<string> known = new();
    FileSystemWatcher? watcher;
    bool stopped;

    /// `maxAge` ignores files older than that (so moving old screenshots in doesn't count); null takes everything new.
    public FolderWatcher(string folder, double? maxAge, Func<string, bool> include, Action<string, DateTime> onNew)
    {
        Folder = folder;
        this.maxAge = maxAge;
        this.include = include;
        this.onNew = onNew;
        debounce.Tick += (_, _) => { debounce.Stop(); Scan(); };
        retry.Tick += (_, _) => Start();
        if (Directory.Exists(folder)) known = new HashSet<string>(Listing());
        Start();
    }

    public void Dispose()
    {
        stopped = true;
        retry.Stop();
        debounce.Stop();
        watcher?.Dispose();
        watcher = null;
    }

    /// Retries until the folder exists (Snipping Tool creates Screenshots on the first save). Each (re)start ends with
    /// a scan against what was known before, so files that arrived while the folder was missing or the watcher was
    /// down still count as new.
    void Start()
    {
        retry.Stop();
        if (stopped) return;
        if (!Directory.Exists(Folder)) { retry.Start(); return; }
        var dispatcher = Dispatcher.CurrentDispatcher;
        watcher = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = false };
        FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
        watcher.Created += changed;
        watcher.Renamed += (s, e) => changed(s, e);
        watcher.Error += (_, _) => dispatcher.BeginInvoke(new Action(() => { watcher?.Dispose(); watcher = null; Start(); }));
        watcher.EnableRaisingEvents = true;
        Scan();
    }

    public static DateTime Added(string path)
    {
        try { return File.GetCreationTime(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// Everything already in the folder from the last `days` days, for backfilling history.
    public IEnumerable<(string Path, DateTime Added)> Existing(double days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        return Listing().Select(p => (p, Added(p))).Where(x => x.Item2 > cutoff).ToList();
    }

    IEnumerable<string> Listing()
    {
        try
        {
            return new DirectoryInfo(Folder).EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & FileAttributes.Hidden) == 0)
                .Select(i => i.FullName).Where(include).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    void Scan()
    {
        if (stopped) return;
        var now = Listing().ToList();
        var fresh = now.Where(p => !known.Contains(p)).ToList();
        known = new HashSet<string>(now);
        DateTime? cutoff = maxAge.HasValue ? DateTime.Now.AddSeconds(-maxAge.Value) : null;
        foreach (var path in fresh.OrderBy(Added))
        {
            var date = Added(path);
            if (cutoff.HasValue && date < cutoff) continue;
            onNew(path, maxAge == null ? DateTime.Now : date);
        }
    }

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
