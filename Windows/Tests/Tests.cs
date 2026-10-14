using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipBubble.Tests;

/// A tiny runner with no test framework, so the repo stays dependency-free. Exit code is the number of failures.
/// `--snapshots <dir>` renders the panel, peek and settings with the press-image data for a side-by-side with the Mac.
static class Program
{
    static readonly string Home = Path.Combine(Path.GetTempPath(), "clipbubble-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    [STAThread]
    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CLIPBUBBLE_HOME", Home);
        Directory.CreateDirectory(Home);
        try
        {
            if (args.Length == 2 && args[0] == "--snapshots") { Snapshots.Run(args[1]); return 0; }
            var failures = 0;
            foreach (var (name, test) in All())
            {
                try { Reset(); test(); Console.WriteLine("ok   " + name); }
                catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + "\n     " + e.Message); }
            }
            Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
            return failures;
        }
        finally
        {
            try { Directory.Delete(Home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void Reset()
    {
        using (var db = new Db()) db.DeleteAll();
        foreach (var c in Prefs.Categories) Prefs.SetOn(c.Key, true);
        if (Directory.Exists(Mascots.Dir)) Directory.Delete(Mascots.Dir, true);
        Mascots.SetActive("blob");
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    static IEnumerable<(string, Action)> All() => new (string, Action)[]
    {
        ("schema matches the Mac app", () =>
        {
            using (new Db()) { }
            var sql = Sql.Query(Paths.Db, "SELECT sql FROM sqlite_master WHERE name IN ('clips','clips_created') ORDER BY name");
            Check(Squash(sql[0]) == Squash(@"CREATE TABLE clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL)"), "clips table: " + sql[0]);
            Check(Squash(sql[1]) == Squash("CREATE INDEX clips_created ON clips(created)"), "index: " + sql[1]);
            Check(Sql.Query(Paths.Db, "PRAGMA journal_mode")[0] == "wal", "WAL mode");
        }),

        ("upsert bumps a repeat and keeps Screenshot/Downloads sources", () =>
        {
            using var db = new Db();
            db.InsertIfMissing(ClipKind.File, @"C:\shots\a.png", "Screenshot", "f:a", DateTime.Now.AddHours(-2));
            db.Upsert(ClipKind.File, @"C:\shots\a.png", null, "Explorer", "f:a");
            var c = db.Clips().Single();
            Check(c.Source == "Screenshot", "source kept: " + c.Source);
            Check((DateTime.Now - c.Created).TotalSeconds < 5, "bumped to now");
            db.Upsert(ClipKind.Text, "hi", null, "Notepad", "t:hi");
            db.Upsert(ClipKind.Text, "hi", null, "Code", "t:hi");
            Check(db.Clips(kind: ClipKind.Text).Single().Source == "Code", "re-copy credits the new app");
            db.Upsert(ClipKind.Text, "hi", null, "Windows Terminal", "t:hi", keepSource: true);
            Check(db.Clips(kind: ClipKind.Text).Single().Source == "Code", "our own write keeps a known clip's source");
            db.Upsert(ClipKind.Text, "from an agent", null, "Windows Terminal", "t:agent", keepSource: true);
            Check(db.Clips(query: "agent").Single().Source == "Windows Terminal", "and gives a new clip the app in front");
        }),

        ("insertIfMissing never overwrites", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "x", null, "A", "t:x");
            db.InsertIfMissing(ClipKind.Text, "x", "B", "t:x", DateTime.Now.AddDays(-1));
            var c = db.Clips().Single();
            Check(c.Source == "A" && (DateTime.Now - c.Created).TotalMinutes < 1, "untouched");
        }),

        ("filters: image files count as images, source ignores case, query, since_hours", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "hello world", null, "Notepad", "t:1");
            db.InsertIfMissing(ClipKind.File, @"C:\pics\cat.JPG", "Screenshot", "f:2", DateTime.Now.AddHours(-3));
            db.InsertIfMissing(ClipKind.File, @"C:\docs\a.pdf", "Downloads", "f:3", DateTime.Now.AddHours(-1));
            Check(db.Clips(kind: ClipKind.Image).Select(c => c.Id).Count() == 1, "jpg file is an image");
            Check(db.Clips(source: "notepad").Count == 1, "NOCASE source");
            Check(db.Clips(query: "world").Count == 1, "query text");
            Check(db.Clips(query: "download").Count == 1, "query source");
            Check(db.Clips(sinceHours: 2).Count == 2, "since_hours");
        }),

        ("switching a category off hides it everywhere", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "secret-ish", null, "Notepad", "t:s");
            db.InsertIfMissing(ClipKind.File, @"C:\d\x.zip", "Downloads", "f:d", DateTime.Now);
            Prefs.SetOn("text", false);
            Check(db.Clips().All(c => c.Kind != ClipKind.Text), "text hidden");
            Prefs.SetOn("downloads", false);
            Check(db.Clips().Count == 0, "downloads hidden");
        }),

        ("prune drops old rows and only our own image copies", () =>
        {
            using var db = new Db();
            Directory.CreateDirectory(Paths.Images);
            var ours = Path.Combine(Paths.Images, "old.png");
            var theirs = Path.Combine(Home, "screenshot.png");
            File.WriteAllBytes(ours, new byte[] { 1 });
            File.WriteAllBytes(theirs, new byte[] { 1 });
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('image','','{ours}','x','i:old',0)");
            db.InsertIfMissing(ClipKind.File, theirs, "Screenshot", "f:theirs", DateTime.Now.AddDays(-30));
            db.Prune();
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM clips")[0] == "0", "rows pruned");
            Check(!File.Exists(ours), "our image copy deleted");
            Check(File.Exists(theirs), "the user's screenshot kept");
        }),

        ("recorder: text, duplicate, and a paste-through rollback", () =>
        {
            var r = new Recorder();
            var t0 = DateTime.Now;
            Check(r.Record(Text("A"), "Notepad", t0), "A recorded");
            Check(!r.Record(Text("A"), "Notepad", t0.AddSeconds(1)), "same again ignored");
            Check(r.Record(Text("B"), "Wispr Flow", t0.AddSeconds(5)), "B recorded");
            Check(r.Record(Text("A"), "Notepad", t0.AddSeconds(6)), "A restored");
            using var db = new Db();
            var texts = db.Clips().Select(c => c.Text).ToList();
            Check(texts.SequenceEqual(new[] { "A" }), "B rolled back: " + string.Join(",", texts));
            Check((DateTime.Now - db.Clips().Single().Created).TotalSeconds < 30, "A not re-bumped");
        }),

        ("recorder: a restore after 2.5 s is a real copy, and an old item is never deleted", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "B", null, "Notepad", "t:" + Store.Sha("B"));
            var r = new Recorder();
            var t0 = DateTime.Now;
            r.Record(Text("A"), "Notepad", t0);
            r.Record(Text("B"), "Notepad", t0.AddSeconds(1));
            r.Record(Text("A"), "Notepad", t0.AddSeconds(2));
            Check(db.Clips().Any(c => c.Text == "B"), "existing B kept");
            r.Record(Text("C"), "Notepad", t0.AddSeconds(10));
            r.Record(Text("A"), "Notepad", t0.AddSeconds(20));
            Check(db.Clips().Any(c => c.Text == "C"), "C kept after 2.5 s");
        }),

        ("recorder: files first, URL+image is an image, text+image is text, blank is nothing", () =>
        {
            var png = TinyPng();
            var r = new Recorder();
            r.Record(new Snapshot(new[] { @"C:\a.txt", @"C:\b.txt" }, "ignored", png, null), "Explorer", DateTime.Now);
            r.Record(new Snapshot(Array.Empty<string>(), "https://example.com/cat.png", png, null), "Edge", DateTime.Now.AddSeconds(10));
            r.Record(new Snapshot(Array.Empty<string>(), "a caption", TinyPng(2), null), "Word", DateTime.Now.AddSeconds(20));
            Check(!r.Record(new Snapshot(Array.Empty<string>(), "   ", null, null), "x", DateTime.Now.AddSeconds(30)), "blank ignored");
            using var db = new Db();
            var clips = db.Clips();
            Check(clips.Any(c => c.Kind == ClipKind.File && c.Paths.Length == 2), "files");
            var img = clips.Single(c => c.Kind == ClipKind.Image);
            Check(img.File != null && File.Exists(img.File) && Path.GetDirectoryName(img.File) == Paths.Images, "image saved as PNG");
            Check(clips.Any(c => c.Kind == ClipKind.Text && c.Text == "a caption"), "text wins over image");
        }),

        ("recorder: a 24-bit clipboard bitmap (DIB) becomes a PNG", () =>
        {
            var r = new Recorder();
            Check(r.Record(new Snapshot(Array.Empty<string>(), null, null, Dib24(3, 2)), "Paint", DateTime.Now), "recorded");
            using var db = new Db();
            var img = db.Clips().Single();
            var frame = BitmapDecoder.Create(new Uri(img.File!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.PixelWidth == 3 && frame.PixelHeight == 2, $"size {frame.PixelWidth}x{frame.PixelHeight}");
        }),

        ("an image copied by an app that only lists Bitmap is still read", () =>
        {
            var dib = Dib24(3, 2);
            var snap = Capture.Read(new FakeClipboard(new[] { "Bitmap" }, f => f == DataFormats.Dib ? new MemoryStream(dib) : null));
            Check(snap?.Dib != null && snap.Dib.Length == dib.Length, "DIB requested even though only Bitmap was listed");
        }),

        ("text from an app that only lists ANSI text is still read", () =>
        {
            var snap = Capture.Read(new FakeClipboard(new[] { DataFormats.Text }, f => f == DataFormats.UnicodeText ? "plain old text" : null));
            Check(snap?.Text == "plain old text", "Unicode text requested even though only Text was listed");
        }),

        ("a delete that fails on a locked database keeps the clip's image", () =>
        {
            var r = new Recorder();
            r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(7), null), "Paint", DateTime.Now);
            using var db = new Db();
            var clip = db.Clips().Single();
            using (Sql.Hold(Paths.Db, "BEGIN IMMEDIATE"))
            {
                try { db.Delete(clip.Id); } catch (InvalidOperationException) { }
            }
            Check(db.Clips().Count == 1 && File.Exists(clip.File!), "row and its image are both still there");
        }),

        ("app names that are really file names read like names", () =>
        {
            Check(Apps.Readable("SnippingTool.exe") == "Snipping Tool", Apps.Readable("SnippingTool.exe"));
            Check(Apps.Readable("Microsoft.Photos.exe") == "Photos", Apps.Readable("Microsoft.Photos.exe"));
            Check(Apps.Readable("msedge.exe") == "msedge", "lowercase names stay");
            Check(Apps.Readable("Google Chrome") == "Google Chrome", "real descriptions are untouched");
        }),

        ("secrets marked by password managers are never read", () =>
        {
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText, Board.ExcludeFormat }, f => f == DataFormats.UnicodeText ? "pw" : null)) == null, "exclude marker");
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText, "CanIncludeInClipboardHistory" }, f => f == DataFormats.UnicodeText ? "pw" : new MemoryStream(new byte[4]))) == null, "history opt-out");
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText }, f => f == DataFormats.UnicodeText ? "hello" : null))?.Text == "hello", "plain text still read");
        }),

        ("recorder: switched-off kinds aren't saved", () =>
        {
            Prefs.SetOn("text", false);
            Check(!new Recorder().Record(Text("nope"), "Notepad", DateTime.Now), "skipped");
            using var db = new Db();
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM clips")[0] == "0", "nothing stored");
        }),

        ("a failed database write throws, and the copy isn't treated as saved", () =>
        {
            using (new Db()) { }
            Sql.Exec(Paths.Db, "CREATE TRIGGER refuse BEFORE INSERT ON clips BEGIN SELECT RAISE(ABORT, 'disk full'); END");
            var r = new Recorder();
            var threw = false;
            try { r.Record(Text("important"), "Notepad", DateTime.Now); } catch (InvalidOperationException) { threw = true; }
            Sql.Exec(Paths.Db, "DROP TRIGGER refuse");
            Check(threw, "the write error surfaced");
            Check(r.Record(Text("important"), "Notepad", DateTime.Now.AddSeconds(1)), "the same text is saved on the next try, not skipped as a duplicate");
            using var db = new Db();
            Check(db.Clips().Single().Text == "important", "stored");
        }),

        ("settings save while another process is reading them, and an unreadable file keeps the last switches", () =>
        {
            Prefs.SetOn("text", false);
            var file = Path.Combine(Paths.Dir, "prefs.json");
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Prefs.SetOn("images", false);
            Prefs.Reload();
            Check(!Prefs.On("text") && !Prefs.On("images"), "both saved");
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Prefs.Reload();
            Check(!Prefs.On("text"), "a locked file doesn't reset hidden categories to visible");
        }),

        ("mascot files keep the Mac JSON shape; remove deletes only its own files", () =>
        {
            var fox = Mascots.Add("Fox", "image", null, new() { ["idle"] = Svg });
            var fox2 = Mascots.Add("Fox", "image", null, new() { ["idle"] = Svg });
            Check(fox.Id == "fox" && fox2.Id == "fox-2", $"ids {fox.Id} {fox2.Id}");
            var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Mascots.Dir, "fox.json")));
            Check(json.Keys.OrderBy(k => k).SequenceEqual(new[] { "id", "idle", "kind", "name" }), "keys " + string.Join(",", json.Keys));
            Check((string)json["idle"] == "fox-idle.svg", "idle file");
            Mascots.SetActive("fox");
            Check(Mascots.Remove("fox"), "removed");
            Check(File.Exists(Path.Combine(Mascots.Dir, "fox-2-idle.svg")) && File.Exists(Path.Combine(Mascots.Dir, "fox-2.json")), "fox-2 untouched");
            Check(Mascots.ActiveId() == "blob", "active falls back to blob");
            Check(!Mascots.Remove("cat"), "built-ins stay");
        }),

        ("removing a mascot never deletes files another mascot owns", () =>
        {
            Mascots.Add("Victim", "blob", "#FF6AD5", new());
            Mascots.Add("Sneaky", "emoji", null, new() { ["idle"] = "victim.json" });
            Check(Mascots.Remove("sneaky"), "removed");
            Check(File.Exists(Path.Combine(Mascots.Dir, "victim.json")), "victim.json survives");
        }),

        ("a hidden category can't be fetched by id either", () =>
        {
            long id;
            using (var db = new Db())
            {
                db.Upsert(ClipKind.Text, "private note", null, "Notepad", "t:private");
                id = db.Clips().Single().Id;
            }
            Prefs.SetOn("text", false);
            using var db2 = new Db();
            Check(db2.Clip(id) == null, "hidden from lookup by id");
            Prefs.SetOn("text", true);
            Check(db2.Clip(id)?.Text == "private note", "visible again when switched back on");
        }),

        ("a screenshot saved into a folder that didn't exist yet is still caught", () =>
        {
            var folder = Path.Combine(Paths.Dir, "later-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var seen = new List<string>();
            using var watcher = new FolderWatcher(folder, 120, _ => true, (path, _) => seen.Add(path));
            Directory.CreateDirectory(folder);
            var shot = Path.Combine(folder, "Screenshot 1.png");
            File.WriteAllBytes(shot, TinyPng());
            Snapshots.Pump(6);   // the watcher re-checks for a missing folder every 5 s
            Check(seen.Contains(shot), "first screenshot reported: " + string.Join(",", seen));
        }),

        ("showing a thumbnail never leaves the user's file locked", () =>
        {
            var shot = Path.Combine(Paths.Dir, "locked-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            File.WriteAllBytes(shot, TinyPng());
            BitmapSource? thumb = null;
            Thumbs.Load(shot, 64, b => thumb = b);
            Snapshots.Pump(1);
            Check(thumb != null && Thumbs.SizeOf(shot) is { Width: 4 }, "thumbnail and size loaded");
            File.Delete(shot);
            Check(!File.Exists(shot), "file could be deleted right away");
        }),

        ("a big animated GIF mascot is decoded at the size it's shown", () =>
        {
            var gif = Path.Combine(Paths.Dir, "big.gif");
            var enc = new GifBitmapEncoder();
            foreach (var shade in new byte[] { 40, 160 })
            {
                var px = new byte[480 * 480 * 4];
                for (var i = 0; i < px.Length; i += 4) { px[i] = shade; px[i + 1] = 90; px[i + 2] = 200; px[i + 3] = 255; }
                enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 480, 96, 96, PixelFormats.Bgra32, null, px, 480 * 4)));
            }
            using (var f = File.Create(gif)) enc.Save(f);
            var image = new System.Windows.Controls.Image();
            Check(GifPlayer.Create(gif, image) != null, "decoded");
            var first = (BitmapSource)image.Source;
            Check(first.PixelWidth <= 102 && first.PixelHeight <= 102, $"frame is {first.PixelWidth}x{first.PixelHeight}; expected at most 102 px, not the GIF's full 480");
        }),

        ("MCP times read the same in every region", () =>
        {
            using (var db = new Db()) db.Upsert(ClipKind.Text, "hello", null, "Notepad", "t:hello");
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");   // writes times as 14.30
            try
            {
                using var db = new Db();
                var text = (string)((Dictionary<string, object>)((object[])ClipBubble.Mcp.Call(db, "clipboard_history", new Dictionary<string, object?>())["content"])[0])["text"];
                Check(System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}"), text);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
        }),

        ("a screenshot that's both copied and saved shows up once, in either order", () =>
        {
            var shot = Path.Combine(Paths.Dir, "Screenshot 1.png");
            File.WriteAllBytes(shot, TinyPng(3));
            var reencoded = Reencode(TinyPng(3));
            using var db = new Db();
            db.InsertIfMissing(ClipKind.File, shot, "Screenshot", "f:" + Store.Sha(shot), DateTime.Now);
            Check(!new Recorder().Record(new Snapshot(Array.Empty<string>(), null, reencoded, null), "Snipping Tool", DateTime.Now), "copy after the file is skipped");
            Check(db.Clips().Count == 1, "file first: one row");

            db.DeleteAll();
            new Recorder().Record(new Snapshot(Array.Empty<string>(), null, reencoded, null), "Snipping Tool", DateTime.Now);
            db.InsertIfMissing(ClipKind.File, shot, "Screenshot", "f:" + Store.Sha(shot), DateTime.Now);
            db.DropCopiesOf(shot);
            Check(db.Clips().Single().Source == "Screenshot", "copy first: the file's row replaces it");

            new Recorder().Record(new Snapshot(Array.Empty<string>(), null, TinyPng(5), null), "Paint", DateTime.Now);
            db.DropCopiesOf(shot);
            Check(db.Clips().Count == 2, "a different picture copied at the same time stays");
        }),

        ("mascot validation matches the Mac messages", () =>
        {
            string Err(Action a) { try { a(); return ""; } catch (MascotException e) { return e.Message; } }
            Check(Err(() => Mascots.Add("x", "robot", null, new())) == "kind must be blob, emoji or image", "kind");
            Check(Err(() => Mascots.Add("x", "blob", "pink", new())) == "blob needs color as a hex like #FF6AD5", "color");
            Check(Err(() => Mascots.Add("x", "emoji", null, new())) == "emoji needs an idle value", "idle");
            Check(Err(() => Mascots.Add("x", "image", null, new() { ["idle"] = "<svg nonsense" })).StartsWith("couldn't read the idle image"), "bad svg");
        }),

        ("color emoji and SVG render with color", () =>
        {
            Check(Colorful(Glyphs.Emoji("🐸", 30)), "frog is green, not monochrome");
            Check(Colorful(Glyphs.Emoji("👩🏽‍🚀", 30)), "ZWJ sequence");
            var svg = Path.Combine(Home, "t.svg");
            File.WriteAllText(svg, Svg);
            Check(Colorful(Glyphs.Load(svg, 34)), "svg");
        }),

        ("spring easing settles at the target and overshoots when underdamped", () =>
        {
            var bouncy = new SpringEase(0.2, 0.45);
            Check(Math.Abs(bouncy.Ease(1) - 1) < 0.01, "settles");
            Check(Enumerable.Range(0, 100).Any(i => bouncy.Ease(i / 100.0) > 1.05), "overshoots");
            var stiff = new SpringEase(0.2, 1);
            Check(Enumerable.Range(0, 100).All(i => stiff.Ease(i / 100.0) <= 1.0001), "critically damped never overshoots");
        }),

        ("MCP over stdio from the real exe: tools, history, image, mascots", Mcp),
    };

    static void Mcp()
    {
        using (var db = new Db())
        {
            db.Upsert(ClipKind.Text, "npm run dev", null, "Windows Terminal", "t:npm");
            var png = Path.Combine(Paths.Images, "x.png");
            File.WriteAllBytes(png, TinyPng());
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('image','','{png}','Paint','i:x',{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
        }
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClipBubble.exe");
        var start = new ProcessStartInfo(exe, "--mcp") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        start.EnvironmentVariables["CLIPBUBBLE_HOME"] = Home;
        using var p = Process.Start(start)!;
        var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var id = 0;
        Dictionary<string, object> Call(string method, object? prms = null)
        {
            stdin.WriteLine(json.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = prms }));
            var line = p.StandardOutput.ReadLine() ?? throw new Exception("server closed");
            var reply = json.Deserialize<Dictionary<string, object>>(line);
            Check((int)reply["id"] == id, "id echoed");
            return (Dictionary<string, object>)reply["result"];
        }
        static object[] List(object o) => ((System.Collections.IList)o).Cast<object>().ToArray();
        string Tool(string name, object args) => (string)((Dictionary<string, object>)List(Call("tools/call", new { name, arguments = args })["content"])[0])["text"];
        try
        {
            stdin.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            stdin.WriteLine("[1,2]");
            stdin.WriteLine("\"not an object\"");
            var init = Call("initialize", new { protocolVersion = "2025-06-18" });
            Check((string)((Dictionary<string, object>)init["serverInfo"])["name"] == "clipbubble", "server name");
            var tools = List(Call("tools/list")["tools"]).Select(t => (string)((Dictionary<string, object>)t)["name"]).ToArray();
            Check(tools.SequenceEqual(new[] { "clipboard_history", "clipboard_get", "clipboard_set", "mascot_list", "mascot_set", "mascot_add", "mascot_remove" }), "tools " + string.Join(",", tools));
            var history = Tool("clipboard_history", new { limit = 5 });
            Check(history.Contains("npm run dev") && history.Contains("· text · Windows Terminal ·"), history);
            Check(Tool("clipboard_history", new { kind = "image" }).Contains("x.png"), "image filter");
            var imageId = int.Parse(Tool("clipboard_history", new { kind = "image" }).Split(' ')[0].TrimStart('#'));
            var got = List(Call("tools/call", new { name = "clipboard_get", arguments = new { id = imageId } })["content"]);
            Check(got.Length == 2 && (string)((Dictionary<string, object>)got[1])["mimeType"] == "image/png", "image block returned");
            Check(Tool("mascot_add", new { name = "Pixel Fox", kind = "image", idle = Svg }) == "Added mascot pixel-fox.", "mascot_add");
            Check(Tool("mascot_list", new { }).Contains("pixel-fox · Pixel Fox · image · custom  ← active"), "mascot_list");
            Check(Tool("mascot_remove", new { id = "pixel-fox" }) == "Removed mascot pixel-fox.", "mascot_remove");
            Check(Tool("clipboard_get", new { id = 999999 }) == "No item with that id.", "missing id");
            stdin.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + (++id) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"clipboard_get\",\"arguments\":{\"id\":9223372036854775808}}}");
            var huge = p.StandardOutput.ReadLine() ?? throw new Exception("server died on an out-of-range id");
            Check(huge.Contains("No item with that id."), "out-of-range id is just a missing item: " + huge);
            Sql.Exec(Paths.Db, "ALTER TABLE clips RENAME TO gone");
            stdin.WriteLine(json.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call", ["params"] = new { name = "clipboard_history", arguments = new { } } }));
            var failed = json.Deserialize<Dictionary<string, object>>(p.StandardOutput.ReadLine() ?? throw new Exception("server died on a failing request"));
            Sql.Exec(Paths.Db, "ALTER TABLE gone RENAME TO clips");
            Check(failed.ContainsKey("error") && (int)failed["id"] == id, "a failing request gets an error reply");
            Check(Tool("clipboard_history", new { limit = 1 }).Contains("#"), "and the server keeps serving");
            stdin.Close();
            Check(p.WaitForExit(5000), "exits when stdin closes");
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    // MARK: - Fixtures

    public const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><rect x=\"2\" y=\"5\" width=\"12\" height=\"7\" fill=\"#E8762B\"/><rect x=\"5\" y=\"7\" width=\"2\" height=\"2\" fill=\"#222\"/></svg>";

    static Snapshot Text(string s) => new(Array.Empty<string>(), s, null, null);

    /// Same pixels, different PNG bytes, like a screenshot tool's copy versus its saved file.
    static byte[] Reencode(byte[] png)
    {
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.On };
        enc.Frames.Add(BitmapFrame.Create(new MemoryStream(png)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static string Squash(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("( ", "(").Replace(" )", ")");

    public static byte[] TinyPng(int seed = 1)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(seed * 40); pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] Dib24(int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (var i = 40; i < dib.Length; i++) dib[i] = 0x80;
        return dib;
    }

    static bool Colorful(BitmapSource? img)
    {
        if (img == null) return false;
        var px = new byte[img.PixelWidth * img.PixelHeight * 4];
        new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0).CopyPixels(px, img.PixelWidth * 4, 0);
        for (var i = 0; i < px.Length; i += 4)
            if (px[i + 3] > 200 && Math.Max(Math.Abs(px[i] - px[i + 1]), Math.Abs(px[i + 1] - px[i + 2])) > 60) return true;
        return false;
    }
}

/// A clipboard that lists `formats` but answers whatever `data` returns, like Windows synthesizing formats on request.
sealed class FakeClipboard(string[] formats, Func<string, object?> data) : IDataObject
{
    public string[] GetFormats() => formats;
    public string[] GetFormats(bool autoConvert) => formats;
    public object? GetData(string format) => data(format);
    public object? GetData(string format, bool autoConvert) => data(format);
    public object? GetData(Type format) => null;
    public bool GetDataPresent(string format) => data(format) != null;
    public bool GetDataPresent(string format, bool autoConvert) => data(format) != null;
    public bool GetDataPresent(Type format) => false;
    public void SetData(object data) { }
    public void SetData(string format, object data) { }
    public void SetData(string format, object data, bool autoConvert) { }
    public void SetData(Type format, object data) { }
}

/// Raw SQLite for fixtures and schema checks, outside the app's own Db.
static class Sql
{
    public static List<string> Query(string path, string sql)
    {
        sqlite3_open16(path, out var db);
        try
        {
            sqlite3_prepare16_v2(db, sql, -1, out var s, IntPtr.Zero);
            var rows = new List<string>();
            while (sqlite3_step(s) == 100) rows.Add(Marshal.PtrToStringUni(sqlite3_column_text16(s, 0)) ?? "");
            sqlite3_finalize(s);
            return rows;
        }
        finally { sqlite3_close_v2(db); }
    }

    public static void Exec(string path, string sql) => Query(path, sql);

    /// Keeps a second connection open with `sql` applied (for example a write lock) until disposed.
    public static IDisposable Hold(string path, string sql)
    {
        sqlite3_open16(path, out var db);
        sqlite3_prepare16_v2(db, sql, -1, out var s, IntPtr.Zero);
        sqlite3_step(s);
        sqlite3_finalize(s);
        return new Closer(() => sqlite3_close_v2(db));
    }

    sealed class Closer(Action close) : IDisposable { public void Dispose() => close(); }

    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
}

/// Renders the real views offscreen at 2x with the same items as Assets/press/panel.png and peek.png.
static class Snapshots
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var fixtures = Path.Combine(Path.GetDirectoryName(Paths.Db)!, "fixtures");
        Directory.CreateDirectory(fixtures);
        var shot = Path.Combine(fixtures, "Screenshot 2026-10-14 214107.png");
        File.WriteAllBytes(shot, Bars(640, 400));
        var photo = Path.Combine(Paths.Images, "sunset.png");
        Directory.CreateDirectory(Paths.Images);
        File.WriteAllBytes(photo, Sunset(1200));
        var pdf = Path.Combine(fixtures, "Pitch Deck v3.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");

        var apps = new Dictionary<string, string>();
        void App(string name, string path) { path = Environment.ExpandEnvironmentVariables(path); if (File.Exists(path)) apps[name] = path; }
        App("Windows Terminal", @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe");
        App("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        App("File Explorer", @"C:\Windows\explorer.exe");
        App("Cursor", @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe");
        App("Visual Studio Code", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe");
        File.WriteAllText(Path.Combine(Paths.Dir, "apps.json"), new JavaScriptSerializer().Serialize(apps));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Row(string kind, string text, string? file, string source, int minutesAgo) =>
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('{kind}','{text.Replace("'", "''")}',{(file == null ? "NULL" : $"'{file}'")},'{source}','{Guid.NewGuid()}',{now - minutesAgo * 60 - 1})");
        using (new Db()) { }
        Row("text", "Meet at 7? Blue Tokai, Indiranagar", null, "WhatsApp", 200);
        Row("text", "func paste(_ clip: Clip) {\n  board.write(clip)\n  hide()\n}", null, "Cursor", 120);
        Row("file", pdf, null, "File Explorer", 60);
        Row("text", "#FF7AB6", null, "Figma", 26);
        Row("image", "", photo, "Snipping Tool", 14);
        Row("text", "https://krnl.studio", null, "Microsoft Edge", 9);
        Row("file", shot, null, "Screenshot", 3);
        Row("text", "npm run dev", null, "Windows Terminal", 0);

        var app = new App();
        var model = new Model();
        model.Reload();
        model.ReloadRecent();
        Pump(1.0);   // thumbnails decode on the thread pool

        var panel = new PanelView(model, () => new SettingsView(model, app));
        model.Selection = model.Clips[1].Id;
        Save(Framed(panel, 16, 420, 470), Path.Combine(dir, "panel.png"));
        model.ShowSettings = true;
        Pump(0.3);
        Save(Framed(panel, 16, 420, 470), Path.Combine(dir, "settings.png"));
        model.ShowSettings = false;

        var peek = new PeekView(model, () => "Ctrl+Shift+Space");
        Save(Framed(peek, 14, 300, double.NaN), Path.Combine(dir, "peek.png"));

        // Every other state the panel can be in.
        void State(string name, Action set, Action reset)
        {
            set();
            Pump(0.6);
            Save(Framed(panel, 16, 420, 470), Path.Combine(dir, name + ".png"));
            reset();
        }
        foreach (var (name, pick) in new (string, Func<Clip, bool>)[] { ("preview-image", c => c.Kind == ClipKind.Image), ("preview-file", c => c.Text.EndsWith(".pdf")), ("preview-text", c => c.Text.StartsWith("func")) })
            State(name, () => { model.Selection = model.Clips.First(pick).Id; model.Previewing = true; }, () => model.Previewing = false);
        State("panel-no-matches", () => model.Query = "zzzz", () => model.Query = "");
        State("panel-filter-screenshots", () => model.Filter = "shots", () => model.Filter = "all");
        State("panel-search", () => model.Query = "krnl", () => model.Query = "");
        model.Unseen = 120;
        var busy = new BubbleView(model) { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
        model.Unseen = 121;
        Pump(0.8);
        Save(busy, Path.Combine(dir, "bubble-badge-99.png"));
        model.Unseen = 0;
        using (var db = new Db()) db.DeleteAll();
        model.Reload();
        model.ReloadRecent();
        State("panel-empty", () => { }, () => { });
        Save(Framed(new PeekView(model, () => "Ctrl+Shift+Space"), 14, 300, double.NaN), Path.Combine(dir, "peek-empty.png"));

        foreach (var m in Mascots.Builtins)
        {
            Mascots.SetActive(m.Id);
            model.LoadMascots();
            foreach (var (mood, hover, happy) in new[] { ("idle", false, false), ("hover", true, false), ("active", false, true) })
            {
                var bubble = new BubbleView(model) { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
                bubble.SetMood(hover, false, happy);
                Pump(0.8);
                Save(bubble, Path.Combine(dir, $"mascot-{m.Id}-{mood}.png"));
            }
        }
        Console.WriteLine("snapshots in " + dir);
    }

    static FrameworkElement Framed(FrameworkElement view, double radius, double width, double height)
    {
        if (view.Parent is System.Windows.Controls.Panel old) old.Children.Remove(view);
        var glass = Glass.Wrap(view, radius, shadow: false);
        glass.Width = width;
        if (!double.IsNaN(height)) glass.Height = height;
        return glass;
    }

    public static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Hosted in an offscreen window, because the history list only creates rows once it's laid out in one.
    static void Save(FrameworkElement e, string path)
    {
        var host = new Window { Content = e, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight };
        host.Show();
        e.UpdateLayout();
        Pump(0.3);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * 2), (int)Math.Ceiling(e.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        rtb.Render(e);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(path)) enc.Save(f);
        host.Content = null;
        host.Close();
    }

    static byte[] Encode(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] Bars(int w, int h)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)), null, new Rect(0, 0, w, h));
            var colors = new[] { Color.FromRgb(0xE5, 0x3E, 0x5A), Color.FromRgb(0xEE, 0x8A, 0x2E), Color.FromRgb(0xB0, 0x3C, 0xD6) };
            for (var i = 0; i < 3; i++) dc.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(40 + i * 190, 40, 170, 180));
            for (var i = 0; i < 4; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), null, new Rect(40, 250 + i * 30, 560 - i * 90, 12));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return Encode(rtb);
    }

    static byte[] Sunset(int size)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF7, 0x8C, 0x6B), Color.FromRgb(0xFB, 0xD2, 0x8B), 90), null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x7A)), null, new Point(size / 2.0, size * 0.55), size * 0.12, size * 0.12);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, size * 0.75), true, true);
                g.LineTo(new Point(size * 0.35, size * 0.55), true, false);
                g.LineTo(new Point(size * 0.6, size * 0.7), true, false);
                g.LineTo(new Point(size * 0.8, size * 0.58), true, false);
                g.LineTo(new Point(size, size * 0.72), true, false);
                g.LineTo(new Point(size, size), true, false);
                g.LineTo(new Point(0, size), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2B, 0x1E, 0x3A)), null, hills);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return Encode(rtb);
    }
}
