using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ClipBubble;

// MARK: - Model

/// The app's state. Views subscribe to `Changed` and redraw the part named in it, like a SwiftUI ObservableObject.
sealed class Model
{
    public static readonly (string Tag, string Title)[] Filters =
        { ("all", "All"), ("text", "Text"), ("image", "Images"), ("file", "Files"), ("shots", "Screenshots"), ("downloads", "Downloads") };

    public event Action<string>? Changed;
    void Raise(string what) => Changed?.Invoke(what);

    public List<Clip> Clips { get; private set; } = new();
    public List<Clip> Recent { get; private set; } = new();
    string query = "", filter = "all";
    public string Query { get => query; set { if (value == query) return; query = value; Raise(nameof(Query)); Reload(); } }
    public string Filter { get => filter; set { if (value == filter) return; filter = value; Raise(nameof(Filter)); Reload(); } }
    long? selection;
    public long? Selection { get => selection; set { selection = value; Raise(nameof(Selection)); } }
    bool previewing, showSettings;
    public bool Previewing { get => previewing; set { previewing = value; Raise(nameof(Previewing)); } }
    public bool ShowSettings { get => showSettings; set { showSettings = value; Raise(nameof(ShowSettings)); } }
    public int Unseen { get => unseen; set { unseen = value; Raise(nameof(Unseen)); } }
    int unseen;
    public Mascot Mascot { get; private set; } = Mascots.Active();
    public List<Mascot> AllMascots { get; private set; } = Mascots.All();
    public readonly Db Db = new();

    public Action<Clip, bool> Pick = (_, _) => { };
    public Action Dismiss = () => { };
    public Action PrefsChanged = () => { };
    /// Bumps on every open: refocuses search and refreshes relative times.
    public void Shown() => Raise(nameof(Shown));

    public Clip? Selected => Clips.FirstOrDefault(c => c.Id == Selection) ?? Clips.FirstOrDefault();

    public void Reload()
    {
        var kind = Store.ParseKind(Filter);
        var source = Filter switch { "shots" => "Screenshot", "downloads" => "Downloads", _ => null };
        Clips = Db.Clips(kind: kind, source: source, query: Query);
        if (!Clips.Any(c => c.Id == Selection)) selection = Clips.FirstOrDefault()?.Id;
        Thumbs.Prewarm(Clips.Take(40));
        Raise(nameof(Clips));
    }

    public void ReloadRecent()
    {
        Recent = Db.Clips(limit: 3);
        Thumbs.Prewarm(Recent);
        Raise(nameof(Recent));
    }

    public void SetPref(string key, bool on)
    {
        Prefs.SetOn(key, on);
        Reload();
        ReloadRecent();
        PrefsChanged();
        Raise("Prefs");
    }

    public void LoadMascots()
    {
        Glyphs.ClearCache();
        AllMascots = Mascots.All();
        Mascot = Mascots.Active();
        Raise(nameof(Mascot));
    }

    public void Move(int delta)
    {
        if (Clips.Count == 0) return;
        var i = Math.Max(0, Clips.FindIndex(c => c.Id == Selection));
        Selection = Clips[Math.Min(Math.Max(i + delta, 0), Clips.Count - 1)].Id;
    }

    public void CycleFilter(int delta)
    {
        var i = Math.Max(0, Array.FindIndex(Filters, f => f.Tag == Filter));
        Filter = Filters[(i + delta + Filters.Length) % Filters.Length].Tag;
    }

    public void Delete(Clip c)
    {
        var i = Math.Max(0, Clips.FindIndex(x => x.Id == c.Id));
        Db.Delete(c.Id);
        Clips.RemoveAll(x => x.Id == c.Id);
        if (Selection == c.Id) selection = Clips.Count == 0 ? null : Clips[Math.Min(i, Clips.Count - 1)].Id;
        Raise(nameof(Clips));
        ReloadRecent();
    }
}

// MARK: - Thumbnails

/// Decodes small thumbnails off the UI thread and keeps them in memory, so rows never flash.
static class Thumbs
{
    static readonly Dictionary<string, BitmapSource?> Cache = new();   // UI thread only
    static readonly Dictionary<string, Size> Dims = new();

    static string Key(string path, int px) => $"{px}|{path}";
    public static BitmapSource? Cached(string path, int px) => Cache.TryGetValue(Key(path, px), out var b) ? b : null;
    public static Size? SizeOf(string path) => Dims.TryGetValue(path, out var s) ? s : null;

    public static void Load(string path, int px, Action<BitmapSource?> done)
    {
        if (Cache.TryGetValue(Key(path, px), out var hit)) { done(hit); return; }
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            BitmapSource? img = null;
            Size? dims = null;
            try
            {
                var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                dims = new Size(frame.PixelWidth, frame.PixelHeight);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (frame.PixelWidth >= frame.PixelHeight) bi.DecodePixelWidth = Math.Min(px, frame.PixelWidth);
                else bi.DecodePixelHeight = Math.Min(px, frame.PixelHeight);
                bi.EndInit();
                bi.Freeze();
                img = bi;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { }
            dispatcher.BeginInvoke(new Action(() =>
            {
                Cache[Key(path, px)] = img;
                if (dims is { } d) Dims[path] = d;
                done(img);
            }));
        });
    }

    public static void Prewarm(IEnumerable<Clip> clips)
    {
        foreach (var c in clips)
            if (c.ImagePath is { } p && !Cache.ContainsKey(Key(p, 64))) Load(p, 64, _ => { });
    }
}

sealed class Thumb : Border
{
    public Thumb(string path, int px, bool fit = false)
    {
        Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
        var image = new Image { Stretch = fit ? Stretch.Uniform : Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Child = image;
        void Show(BitmapSource? b) { image.Source = b; if (b != null) Background = Brushes.Transparent; }
        if (Thumbs.Cached(path, px) is { } hit) Show(hit);
        else Thumbs.Load(path, px, Show);
    }
}

// MARK: - Theme and helpers

/// Fonts, colors and motion copied from the Mac app's SwiftUI values.
static class UI
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly Color Panel = Color.FromRgb(0x21, 0x1F, 0x1C);   // sampled from Assets/press/panel.png
    public static readonly Brush Primary = White(0.85), Secondary = White(0.55), Tertiary = White(0.25);
    public static readonly Color Accent = AccentColor();
    public static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Accent));
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    public static Brush White(double opacity) => Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// The Windows accent color, like the Mac's system accent.
    static Color AccentColor()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    public static string Glyph(int code) => ((char)code).ToString();
    public static readonly string
        Search = Glyph(0xE721), Clear = Glyph(0xEB90), Gear = Glyph(0xE713), Clipboard = Glyph(0xE77F), AlignLeft = Glyph(0xE8E4),
        Link = Glyph(0xE71B), Camera = Glyph(0xE722), Download = Glyph(0xE896), Back = Glyph(0xE76B), Ok = Glyph(0xEC61),
        Warning = Glyph(0xE7BA), Off = Glyph(0xE738), Plus = Glyph(0xE710), Copy = Glyph(0xE8C8), Check = Glyph(0xE73E),
        Power = Glyph(0xE7E8), Keyboard = Glyph(0xE765);

    /// Line height follows SF Pro (1.2x) rather than Segoe's taller default, so rows sit where they do on the Mac.
    public static TextBlock Label(string text, double size, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text, FontFamily = Text, FontSize = size, Foreground = color ?? Primary, FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = Math.Round(size * 1.2),
    };

    public static TextBlock Icon(string glyph, double size, Brush? color = null) =>
        new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = color ?? Secondary, VerticalAlignment = VerticalAlignment.Center };

    /// WPF has no letter spacing, so tracked caps are laid out one letter at a time.
    public static FrameworkElement Tracked(string text, double size, double tracking, Brush color, FontWeight weight)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ch in text) row.Children.Add(new TextBlock { Text = ch.ToString(), FontFamily = Text, FontSize = size, FontWeight = weight, Foreground = color, Margin = new Thickness(0, 0, tracking, 0) });
        return row;
    }

    public static Border Divider() => new() { Height = 1, Background = White(0.07) };

    /// `#RRGGBB` or `RRGGBB` to a color.
    public static Color? HexColor(string s)
    {
        var t = s.Trim().TrimStart('#');
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static bool IsUrl(string s)
    {
        var t = s.Trim();
        return (t.StartsWith("http://") || t.StartsWith("https://")) && !t.Any(char.IsWhiteSpace);
    }

    public static string Ago(DateTime d)
    {
        var s = (int)(DateTime.Now - d).TotalSeconds;
        if (s < 60) return "now";
        if (s < 3600) return $"{s / 60}m";
        if (s < 86400) return $"{s / 3600}h";
        return $"{s / 86400}d";
    }

    /// `C:\Users\me\Documents` as `~\Documents`, like the Mac's abbreviatingWithTildeInPath.
    public static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path.Substring(home.Length) : path;
    }

    /// A shallow curve: `up` is a "^" (happy eye); otherwise a smile, optionally closed into an open mouth.
    public static System.Windows.Shapes.Path Arc(double w, double h, bool up, bool closed = false, Color? fill = null, Color? stroke = null, double thickness = 0)
    {
        var y0 = up ? h : 0;
        var ctrl = up ? -h : h * 2;
        var fig = new PathFigure { StartPoint = new Point(0, y0), IsClosed = closed, IsFilled = closed };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, ctrl), new Point(w, y0), true));
        return new System.Windows.Shapes.Path
        {
            Width = w, Height = h, Data = new PathGeometry(new[] { fig }),
            Fill = fill is { } f ? new SolidColorBrush(f) : null,
            Stroke = stroke is { } s ? new SolidColorBrush(s) : null,
            StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    public static readonly IEasingFunction Cubic = new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// SwiftUI's `.spring(response:dampingFraction:)`, run for as long as the spring takes to settle.
    public static void Spring(Animatable target, DependencyProperty prop, double to, double response, double damping)
    {
        var ease = new SpringEase(response, damping);
        target.BeginAnimation(prop, new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(ease.Settle))) { EasingFunction = ease });
    }

    public static void Spring(UIElement target, DependencyProperty prop, double to, double response, double damping)
    {
        var ease = new SpringEase(response, damping);
        target.BeginAnimation(prop, new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(ease.Settle))) { EasingFunction = ease });
    }

    public static void Fade(UIElement target, double to, double seconds) =>
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(seconds))));

    /// SwiftUI keyframe tracks: each step animates to `value` over `seconds` with its own curve.
    public static DoubleAnimationUsingKeyFrames Keyframes(params (double Value, double Seconds, IEasingFunction Ease)[] steps)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        var t = 0.0;
        foreach (var (value, seconds, ease) in steps)
        {
            t += seconds;
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), ease));
        }
        return anim;
    }

    public static BitmapSource? ShellIcon(string path, int px) => Shell.Icon(path, px);
}

/// A damped spring with SwiftUI's parameters: `response` is the period, `damping` the damping fraction.
sealed class SpringEase : EasingFunctionBase
{
    readonly double omega, zeta;
    public readonly double Settle;

    public SpringEase(double response, double damping)
    {
        omega = 2 * Math.PI / response;
        zeta = damping;
        Settle = Math.Min(2, -Math.Log(0.001) / (zeta * omega));
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalized)
    {
        var t = normalized * Settle;
        if (zeta >= 1) return 1 - (1 + omega * t) * Math.Exp(-omega * t);
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * omega * t) * (Math.Cos(wd * t) + zeta * omega / wd * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase(2 * Math.PI / omega, zeta);
}

/// Icons for files and apps from the Windows shell, with transparency intact.
static unsafe class Shell
{
    static readonly Dictionary<string, BitmapSource?> Cache = new();
    static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static BitmapSource? Icon(string path, int px)
    {
        var key = $"{px}|{path}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        BitmapSource? result = null;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory) >= 0)
        {
            IntPtr hbmp;
            // IShellItemImageFactory::GetImage, slot 3. SIIGBF_ICONONLY so files show their type icon like Finder's.
            if (((delegate* unmanaged[Stdcall]<IntPtr, Win.SIZE, int, IntPtr*, int>)(*(IntPtr**)factory)[3])(factory, new Win.SIZE { cx = px, cy = px }, 0x4, &hbmp) >= 0)
            {
                result = FromHBitmap(hbmp);
                DeleteObject(hbmp);
            }
            Marshal.Release(factory);
        }
        return Cache[key] = result;
    }

    /// Reads the 32-bit premultiplied pixels directly; WPF's own HBITMAP conversion drops the alpha.
    static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        var bm = new BITMAP();
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return null;
        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * bm.bmHeight];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < bm.bmHeight; y++) Buffer.BlockCopy(pixels, y * stride, flipped, (bm.bmHeight - 1 - y) * stride, stride);
        var img = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, flipped, stride);
        img.Freeze();
        return img;
    }

    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, in Guid riid, out IntPtr item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// The Windows folder picker (NSOpenPanel with canChooseDirectories). Returns null if cancelled.
    public static string? PickFolder(IntPtr owner, string start, string title)
    {
        if (CoCreateInstance(new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), IntPtr.Zero, 1, new Guid("d57c7288-d4ad-4768-be02-9d969532d960"), out var dlg) < 0) return null;
        try
        {
            var vt = *(IntPtr**)dlg;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[9])(dlg, 0x20 | 0x40);   // SetOptions: FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
            fixed (char* t = title) ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vt[17])(dlg, t);   // SetTitle
            if (Directory.Exists(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), out var folder) >= 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[12])(dlg, folder);   // SetFolder
                Marshal.Release(folder);
            }
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[3])(dlg, owner) < 0) return null;   // Show
            IntPtr item;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[20])(dlg, &item) < 0) return null;   // GetResult
            try
            {
                IntPtr name;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)item)[5])(item, 0x80058000, &name) < 0) return null;   // GetDisplayName(FILESYSPATH)
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(dlg); }
    }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int ctx, in Guid riid, out IntPtr obj);
}

// MARK: - Windows

/// The Win32 calls the bubble, panel and peek need for focus, position and pasting.
static class Win
{
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    public static void NoActivate(Window w, bool noActivate)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLong(h, -20) | 0x80;   // WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab
        if (noActivate) ex |= 0x08000000;         // WS_EX_NOACTIVATE
        SetWindowLong(h, -20, ex);
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    public static RECT Frame(Window w) { GetWindowRect(Handle(w), out var r); return r; }

    public static void Move(Window w, int x, int y) => SetWindowPos(Handle(w), IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE NOZORDER NOACTIVATE

    public static double Scale(Window w) => GetDpiForWindow(Handle(w)) / 96.0;

    /// The work area (screen minus taskbar) of the monitor containing `p`, in physical pixels.
    public static RECT WorkArea(POINT p)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(p, 2), ref info);
        return info.rcWork;
    }

    public static POINT Cursor() { GetCursorPos(out var p); return p; }

    public static bool ModifiersDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    public static void SendPaste()
    {
        INPUT Key(ushort vk, bool up) => new() { type = 1, ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } };
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// Windows won't let a normal app type into one running as administrator; those just get the copy.
    public static bool CanPasteInto(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(0x1000, false, (int)pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return false;   // elevated processes refuse TOKEN_QUERY
            CloseHandle(token);
            return true;
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}

/// A borderless always-on-top window. Bubble and peek never take focus; the panel does, because it has a search field.
sealed class FloatingWindow : Window
{
    public FloatingWindow(double width, double height, bool keyable)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = keyable;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SourceInitialized += (_, _) => Win.NoActivate(this, !keyable);
    }
}

/// The frosted surface the Mac draws with NSVisualEffectView: here the panel's sampled color, rounded,
/// with the hairline border drawn over the content like SwiftUI's strokeBorder overlay.
static class Glass
{
    public static FrameworkElement Wrap(FrameworkElement content, double radius)
    {
        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = new SolidColorBrush(UI.Panel) });
        grid.Children.Add(content);
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), BorderBrush = UI.White(0.1), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        return grid;
    }
}

// MARK: - Bubble face

/// The 64 x 64 bubble: glass circle, mascot, unseen badge and the ring that flashes on capture.
sealed class BubbleView : Grid
{
    readonly Model model;
    readonly Ellipse ring;
    readonly ScaleTransform ringScale = new(1, 1);
    readonly ScaleTransform hoverScale = new(1, 1);
    readonly Grid face;
    readonly Border badge;
    readonly TextBlock badgeText;
    MascotView mascot;
    public readonly Ellipse HitArea;

    public BubbleView(Model model)
    {
        this.model = model;
        Width = Height = 64;
        face = new Grid { Width = 46, Height = 46, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = hoverScale };
        face.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0xE6, UI.Panel.R, UI.Panel.G, UI.Panel.B)) });
        ring = new Ellipse { Stroke = UI.AccentBrush, StrokeThickness = 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale };
        face.Children.Add(ring);
        face.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromArgb(41, 255, 255, 255), Colors.Transparent, new Point(0.5, 0), new Point(0.5, 0.5)) });
        face.Children.Add(new Ellipse { StrokeThickness = 1, Stroke = new LinearGradientBrush(Color.FromArgb(115, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 90) });
        mascot = new MascotView(model.Mascot);
        face.Children.Add(mascot);
        Children.Add(face);

        badgeText = new TextBlock { FontFamily = UI.Text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        badge = new Border
        {
            MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(9),
            Background = UI.AccentBrush, BorderBrush = UI.White(0.35), BorderThickness = new Thickness(1), Child = badgeText,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9 - 5, 9 - 5, 0), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        Children.Add(badge);
        HitArea = new Ellipse { Width = 46, Height = 46, Fill = Brushes.Transparent };
        Children.Add(HitArea);
        model.Changed += what =>
        {
            if (what == nameof(Model.Unseen)) UpdateBadge();
            if (what == nameof(Model.Mascot)) ReplaceMascot();
        };
    }

    void ReplaceMascot()
    {
        face.Children.Remove(mascot);
        mascot = new MascotView(model.Mascot);
        face.Children.Add(mascot);
    }

    public void SetMood(bool hover, bool pressed, bool happy)
    {
        mascot.Update(hover, pressed, happy);
        UI.Spring(hoverScale, ScaleTransform.ScaleXProperty, hover ? 1.06 : 1, 0.2, 0.7);
        UI.Spring(hoverScale, ScaleTransform.ScaleYProperty, hover ? 1.06 : 1, 0.2, 0.7);
    }

    public void Wiggle() => mascot.Wiggle();

    /// The mascot hops and the accent ring ripples out.
    public void Pulse()
    {
        mascot.Hop();
        var grow = new DoubleAnimation(1, 1.55, TimeSpan.FromSeconds(0.7)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(0.7)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    void UpdateBadge()
    {
        var show = model.Unseen > 0;
        badgeText.Text = Math.Min(model.Unseen, 99).ToString();
        var scale = (ScaleTransform)badge.RenderTransform;
        if (show && badge.Visibility != Visibility.Visible)
        {
            badge.Visibility = Visibility.Visible;
            scale.ScaleX = scale.ScaleY = 0;
            badge.Opacity = 0;
            UI.Spring(scale, ScaleTransform.ScaleXProperty, 1, 0.25, 0.7);
            UI.Spring(scale, ScaleTransform.ScaleYProperty, 1, 0.25, 0.7);
            UI.Fade(badge, 1, 0.2);
        }
        else if (!show) badge.Visibility = Visibility.Collapsed;
    }
}

// MARK: - Rows

/// One history item: tile, title, source and age, and the Ctrl+N hint for the first nine.
sealed class Row : Border
{
    readonly Clip clip;
    readonly Model model;
    readonly bool selected;
    Point? down;

    public Row(Clip clip, int index, bool selected, Model model)
    {
        this.clip = clip;
        this.model = model;
        this.selected = selected;
        Height = 46;
        Padding = new Thickness(8, 0, 8, 0);
        CornerRadius = new CornerRadius(9);
        Background = selected ? UI.White(0.12) : Brushes.Transparent;
        Cursor = Cursors.Arrow;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Tile(clip) { Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(tile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = UI.Label(Title(), 13, UI.Primary, FontWeights.Medium);
        texts.Children.Add(title);
        var meta = new DockPanel { Margin = new Thickness(0, 2, 0, 0), LastChildFill = true };
        if (SourceIcon() is { } icon) { icon.Margin = new Thickness(0, 0, 4, 0); meta.Children.Add(icon); }
        meta.Children.Add(UI.Label(Subtitle(), 11, UI.Secondary));
        texts.Children.Add(meta);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (index is >= 0 and < 9)
        {
            var hint = UI.Label($"Ctrl+{index + 1}", 11, selected ? UI.Secondary : UI.Tertiary, FontWeights.Medium);
            hint.Margin = new Thickness(6, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
        }
        Child = grid;

        // An image's size is known once its thumbnail has decoded.
        if (clip.Kind == ClipKind.Image && clip.ImagePath is { } p && Thumbs.SizeOf(p) == null)
            Thumbs.Load(p, 64, _ => title.Text = Title());

        MouseEnter += (_, _) => { if (!selected) Background = UI.White(0.06); };
        MouseLeave += (_, _) => { if (!selected) Background = Brushes.Transparent; down = null; };
        MouseLeftButtonDown += (_, e) => down = e.GetPosition(this);
        MouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - d.X) < 4 && Math.Abs(p.Y - d.Y) < 4) return;
            down = null;
            DragDrop.DoDragDrop(this, DragData(), DragDropEffects.Copy);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (down == null) return;
            down = null;
            model.Pick(clip, !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        };
        ContextMenu = Menu();
    }

    ContextMenu Menu()
    {
        var menu = new ContextMenu();
        void Add(string title, Action act) { var item = new MenuItem { Header = title }; item.Click += (_, _) => act(); menu.Items.Add(item); }
        Add("Paste", () => model.Pick(clip, true));
        Add("Copy", () => model.Pick(clip, false));
        if ((clip.ImagePath ?? clip.Paths.FirstOrDefault()) is { } p)
        {
            Add("Open", () => { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (Win32Exception) { } });
            Add("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{p}\""));
        }
        menu.Items.Add(new Separator());
        Add("Delete", () => model.Delete(clip));
        return menu;
    }

    FrameworkElement? SourceIcon() => clip.Source switch
    {
        "Screenshot" => UI.Icon(UI.Camera, 9.5),
        "Downloads" => UI.Icon(UI.Download, 9.5),
        _ => Apps.ExePath(clip.Source) is { } exe && UI.ShellIcon(exe, 32) is { } img ? new Image { Source = img, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center } : null,
    };

    string FirstLine => clip.Text.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

    string Title()
    {
        switch (clip.Kind)
        {
            case ClipKind.Image:
                return clip.ImagePath is { } p && Thumbs.SizeOf(p) is { } s ? $"Image  {s.Width}×{s.Height}" : "Image";
            case ClipKind.File:
                return string.Join(", ", clip.Paths.Select(Path.GetFileName));
            default:
                var t = FirstLine.Length > 200 ? FirstLine.Substring(0, 200) : FirstLine;
                return UI.IsUrl(t) ? t.Replace("https://", "").Replace("http://", "") : t;
        }
    }

    string Subtitle()
    {
        var parts = new List<string> { clip.Source, UI.Ago(clip.Created) };
        if (clip.Kind == ClipKind.File && clip.Paths.FirstOrDefault() is { } p) parts.Add(UI.Tilde(Path.GetDirectoryName(p) ?? ""));
        else if (clip.Kind == ClipKind.Text)
        {
            var lines = clip.Text.Split('\n').Count(l => l.Trim('\r').Length > 0);
            if (lines > 1) parts.Add($"{lines} lines");
        }
        return string.Join(" · ", parts);
    }

    DataObject DragData()
    {
        if ((clip.ImagePath ?? clip.Paths.FirstOrDefault()) is { } p && File.Exists(p))
        {
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { p });
            return data;
        }
        return new DataObject(DataFormats.UnicodeText, clip.Text);
    }
}

sealed class Tile : Border
{
    public Tile(Clip clip)
    {
        Width = Height = 30;
        CornerRadius = new CornerRadius(7);
        ClipToBounds = true;
        BorderBrush = UI.White(0.08);
        BorderThickness = new Thickness(1);
        if (clip.ImagePath is { } img)
        {
            Child = new Thumb(img, 64);
            Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 7, 7);
        }
        else if (clip.Paths.FirstOrDefault() is { } p)
        {
            BorderThickness = new Thickness(0);
            Child = new Image { Source = UI.ShellIcon(p, 64), Margin = new Thickness(2) };
        }
        else if (UI.HexColor(clip.Text) is { } c) Background = new SolidColorBrush(c);
        else if (UI.IsUrl(clip.Text)) Symbol(UI.Link, Color.FromRgb(0x0A, 0x84, 0xFF), angle: -45);   // SF Symbols' link is diagonal
        else
        {
            // SF Symbols' text.alignleft: four rounded lines, heavier than the Fluent glyph.
            Background = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            var lines = new Canvas { Width = 12, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (y, w) in new[] { (0.75, 12.0), (3.6, 8.0), (6.4, 12.0), (9.25, 7.0) })
                lines.Children.Add(new Line { X1 = 0.75, X2 = w - 0.75, Y1 = y, Y2 = y, Stroke = UI.White(0.85), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            Child = lines;
        }
    }

    void Symbol(string glyph, Color tint, double angle = 0)
    {
        Background = new SolidColorBrush(Color.FromArgb(31, tint.R, tint.G, tint.B));
        Child = new TextBlock
        {
            Text = glyph, FontFamily = UI.Icons, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(217, tint.R, tint.G, tint.B)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle),
        };
    }
}

// MARK: - Panel

sealed class PanelView : Grid
{
    readonly Model model;
    readonly TextBox search;
    readonly TextBlock placeholder;
    readonly Border clear;
    readonly StackPanel filters = new() { Orientation = Orientation.Horizontal };
    readonly Grid listArea = new();
    readonly ScrollViewer scroll;
    readonly StackPanel rows = new() { Margin = new Thickness(6) };
    readonly Grid main = new();
    readonly TranslateTransform lift = new();
    SettingsView? settings;

    public PanelView(Model model, Func<SettingsView> makeSettings)
    {
        this.model = model;
        Width = 420;
        Height = 470;
        RenderTransform = lift;
        foreach (var h in new[] { 50, 0, 1, -1, 1, 36 })
            main.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(1, GridUnitType.Star) : h == 0 ? GridLength.Auto : new GridLength(h) });

        var header = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.Icon(UI.Search, 16.5, UI.Secondary));
        search = new TextBox
        {
            FontFamily = UI.Text, FontSize = 16, Foreground = UI.Primary, CaretBrush = UI.Primary, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            SelectionBrush = UI.AccentBrush, FocusVisualStyle = null,
        };
        placeholder = UI.Label("Search clipboard…", 16, UI.White(0.8));
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        Grid.SetColumn(placeholder, 1);
        Grid.SetColumn(search, 1);
        header.Children.Add(placeholder);
        header.Children.Add(search);
        clear = new Border { Child = UI.Icon(UI.Clear, 13, UI.Tertiary), Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow };
        clear.MouseLeftButtonUp += (_, _) => model.Query = "";
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);
        search.TextChanged += (_, _) => { model.Query = search.Text; placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        Add(header, 0);

        var filterRow = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        filterRow.Children.Add(filters);
        var tab = UI.Label("tab", 10, UI.Tertiary, FontWeights.Medium);
        tab.HorizontalAlignment = HorizontalAlignment.Right;
        tab.VerticalAlignment = VerticalAlignment.Center;
        filterRow.Children.Add(tab);
        Add(filterRow, 1);
        Add(UI.Divider(), 2);

        scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
        listArea.Children.Add(scroll);
        Add(listArea, 3);
        Add(UI.Divider(), 4);
        Add(Footer(), 5);
        Children.Add(main);

        model.Changed += what =>
        {
            switch (what)
            {
                case nameof(Model.Clips): RenderRows(); break;
                case nameof(Model.Selection): RenderRows(scrollToSelection: true); break;
                case nameof(Model.Filter): RenderFilters(); break;
                case nameof(Model.Query):
                    if (search.Text != model.Query) search.Text = model.Query;
                    clear.Visibility = model.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(Model.Previewing): RenderRows(); break;
                case nameof(Model.ShowSettings):
                    if (model.ShowSettings) { settings = makeSettings(); Children.Add(settings); main.Visibility = Visibility.Hidden; }
                    else { if (settings != null) Children.Remove(settings); settings = null; main.Visibility = Visibility.Visible; search.Focus(); }
                    break;
                case nameof(Model.Shown): Opened(); break;
            }
        };
        RenderFilters();
    }

    void Add(UIElement e, int row) { Grid.SetRow(e, row); main.Children.Add(e); }

    void Opened()
    {
        search.Focus();
        Keyboard.Focus(search);
        lift.Y = 6;
        UI.Spring(lift, TranslateTransform.YProperty, 0, 0.2, 0.9);
        RenderRows();
        scroll.ScrollToTop();
    }

    void RenderFilters()
    {
        filters.Children.Clear();
        foreach (var (tag, title) in Model.Filters)
        {
            var on = model.Filter == tag;
            var pill = new Border
            {
                Padding = new Thickness(7, 4, 7, 4), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 2, 0),
                Background = on ? UI.White(0.14) : Brushes.Transparent,
                Child = UI.Label(title, 11.5, on ? UI.Primary : UI.Secondary, FontWeights.Medium),
            };
            pill.MouseLeftButtonUp += (_, _) => model.Filter = tag;
            filters.Children.Add(pill);
        }
    }

    void RenderRows(bool scrollToSelection = false)
    {
        listArea.Children.Clear();
        listArea.Children.Add(scroll);
        rows.Children.Clear();
        if (model.Clips.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = UI.Icon(UI.Clipboard, 26, UI.Tertiary);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var text = UI.Label(model.Query.Length == 0 ? "Copy anything — it lands here" : "No matches", 12, UI.Secondary);
            text.Margin = new Thickness(0, 8, 0, 0);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(text);
            listArea.Children.Add(empty);
            return;
        }
        Row? selectedRow = null;
        for (var i = 0; i < model.Clips.Count; i++)
        {
            var c = model.Clips[i];
            var row = new Row(c, i, c.Id == model.Selection, model) { Margin = new Thickness(0, 0, 0, 1) };
            if (c.Id == model.Selection) selectedRow = row;
            rows.Children.Add(row);
        }
        if (scrollToSelection && selectedRow != null) selectedRow.BringIntoView();
        if (model.Previewing && model.Selected is { } sel) listArea.Children.Add(new PreviewPane(sel));
    }

    FrameworkElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        var gear = new Border { Child = UI.Icon(UI.Gear, 14, UI.Secondary), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Settings" };
        gear.MouseLeftButtonUp += (_, _) => model.ShowSettings = true;
        footer.Children.Add(gear);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (k, label) in new[] { ("↩", "Paste"), ("Alt ↩", "Copy"), ("Ctrl Y", "Preview"), ("Ctrl ⌫", "Delete") })
            keys.Children.Add(Key(k, label, keys.Children.Count == 0 ? 0 : 14));
        footer.Children.Add(keys);
        return footer;
    }

    public static FrameworkElement Key(string keys, string label, double left = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(left, 0, 0, 0) };
        var text = UI.Label(label, 11, UI.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(new Border
        {
            Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(5, 1, 5, 2), CornerRadius = new CornerRadius(4), Background = UI.White(0.09),
            Child = UI.Label(keys, 10.5, UI.Secondary, FontWeights.SemiBold),
        });
        return row;
    }

    public void FocusSearch() { search.Focus(); Keyboard.Focus(search); }
}

/// Ctrl+Y: the selected item at full size.
sealed class PreviewPane : Border
{
    public PreviewPane(Clip clip)
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0E, 0x0D));
        if (clip.ImagePath is { } p) Child = new Thumb(p, 1400, fit: true) { Margin = new Thickness(12), Background = Brushes.Transparent };
        else if (clip.Paths.Length > 0)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
            stack.Children.Add(new Image { Source = UI.ShellIcon(clip.Paths[0], 128), Width = 64, Height = 64 });
            stack.Children.Add(new TextBox
            {
                Text = string.Join("\n", clip.Paths), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = UI.Secondary, FontFamily = UI.Text, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            });
            Child = stack;
        }
        else
        {
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = new TextBox
                {
                    Text = clip.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UI.Primary,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(14),
                },
            };
        }
    }
}

/// Hovering the bubble: the last three items, one click to paste.
sealed class PeekView : StackPanel
{
    readonly Model model;
    readonly Func<string> hotkey;

    public PeekView(Model model, Func<string> hotkey)
    {
        this.model = model;
        this.hotkey = hotkey;
        Width = 288;   // 300 with the padding, like SwiftUI's .padding(6).frame(width: 300)
        Margin = new Thickness(6);
        model.Changed += what => { if (what is nameof(Model.Recent) or nameof(Model.Shown)) Render(); };
        Render();
    }

    void Render()
    {
        Children.Clear();
        // The Mac peek's header sits 8 pt lower than its padding suggests (measured from Assets/press/peek.png).
        var header = new Grid { Margin = new Thickness(8, 12, 8, 3) };
        header.Children.Add(UI.Tracked("RECENT", 10, 0.6, UI.Tertiary, FontWeights.SemiBold));
        var hint = UI.Label($"{hotkey()} for all", 10, UI.Tertiary);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(hint);
        Children.Add(header);
        if (model.Recent.Count == 0)
        {
            var empty = UI.Label("Nothing copied yet", 12, UI.Secondary);
            empty.Margin = new Thickness(8);
            Children.Add(empty);
        }
        foreach (var c in model.Recent) Children.Add(new Row(c, -1, false, model) { Margin = new Thickness(0, 1, 0, 0) });
    }
}

// MARK: - Settings

/// Settings, shown in place of the list (gear in the footer, Esc to go back).
sealed class SettingsView : Grid
{
    readonly Model model;
    readonly App app;
    readonly StackPanel content = new() { Margin = new Thickness(16) };
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(2) };

    public SettingsView(Model model, App app)
    {
        this.model = model;
        this.app = app;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        var back = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
        back.Children.Add(UI.Icon(UI.Back, 12, UI.Secondary));
        var title = UI.Label("Settings", 16, UI.Primary, FontWeights.SemiBold);
        title.Margin = new Thickness(6, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        back.Children.Add(title);
        back.MouseLeftButtonUp += (_, _) => model.ShowSettings = false;
        header.Children.Add(back);
        var esc = UI.Label("esc", 10, UI.Tertiary, FontWeights.Medium);
        esc.HorizontalAlignment = HorizontalAlignment.Right;
        esc.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(esc);
        Children.Add(header);
        var divider = UI.Divider();
        SetRow(divider, 1);
        Children.Add(divider);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
        SetRow(scroll, 2);
        Children.Add(scroll);

        Render();
        ticker.Tick += (_, _) => Render();
        Loaded += (_, _) => ticker.Start();
        Unloaded += (_, _) => ticker.Stop();
        model.Changed += what => { if (what is "Prefs" or nameof(Model.Mascot)) Render(); };
    }

    /// Rebuilt whole (it's small): picks up folder changes made outside the app, like the Mac's 2 s refresh.
    void Render()
    {
        content.Children.Clear();

        var save = Section("Save to history");
        foreach (var c in Prefs.Categories.Where(c => c.Key is not ("screenshots" or "downloads")))
            save.Children.Add(Row(c.Icon, c.Title, c.Detail, Switch(Prefs.On(c.Key), on => model.SetPref(c.Key, on))));
        save.Children.Add(Note("Turning one off stops saving it and hides what's already there, for your agents too."));

        var folders = Section("Folders & permissions");
        var shots = FolderWatcher.ScreenshotFolder;
        var custom = Prefs.Get<string?>("screenshotFolder", null) is { Length: > 0 };
        var shotExtra = new StackPanel();
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        links.Children.Add(Link("Change folder…", PickShotsFolder));
        if (custom) links.Children.Add(Link("Use Pictures\\Screenshots", () => { Prefs.Set("screenshotFolder", null); model.SetPref("screenshots", true); }, 12));
        shotExtra.Children.Add(links);
        shotExtra.Children.Add(Check("Also copy new ones to the clipboard", Prefs.Get("copyScreenshots", true), on => Prefs.Set("copyScreenshots", on)));
        folders.Children.Add(PermRow(UI.Camera, "Screenshots", shots, custom ? "Folder you picked" : "Where Windows and Snipping Tool save them",
            Prefs.On("screenshots") ? FolderWatcher.CanRead(shots) : null, "screenshots", shotExtra));
        var downloads = FolderWatcher.DownloadsFolder;
        folders.Children.Add(PermRow(UI.Download, "Recent downloads", downloads, null,
            Prefs.On("downloads") ? FolderWatcher.CanRead(downloads) : null, "downloads", null));
        folders.Children.Add(Row(UI.Keyboard, "Auto-paste", "Enter pastes into the app you were in. Apps running as administrator just get a copy.",
            Switch(Prefs.Get("autoPaste", true), on => Prefs.Set("autoPaste", on))));
        folders.Children.Add(Row(UI.Keyboard, "Open ClipBubble", null, HotkeyBox()));

        var mascots = Section("Mascot");
        var grid = new WrapPanel { Width = 7 * 50 };
        foreach (var m in model.AllMascots)
        {
            var active = m.Id == model.Mascot.Id;
            var cell = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 8, 8), Background = Brushes.Transparent, ToolTip = m.Name };
            cell.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(23, 23, 23)), Stroke = active ? UI.AccentBrush : UI.White(0.1), StrokeThickness = active ? 2 : 1 });
            cell.Children.Add(new MascotView(m) { LayoutTransform = new ScaleTransform(0.8, 0.8) });
            cell.MouseLeftButtonUp += (_, _) => { Mascots.SetActive(m.Id); model.LoadMascots(); };
            grid.Children.Add(cell);
        }
        var plus = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 8, 8), Background = Brushes.Transparent, ToolTip = "Add your own (PNG, GIF, SVG)" };
        plus.Children.Add(new Ellipse { Stroke = UI.White(0.18), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var plusIcon = UI.Icon(UI.Plus, 14, UI.Secondary);
        plusIcon.HorizontalAlignment = HorizontalAlignment.Center;
        plus.Children.Add(plusIcon);
        plus.MouseLeftButtonUp += (_, _) => AddMascot();
        grid.Children.Add(plus);
        mascots.Children.Add(grid);

        var agents = Section("AI agents (MCP)");
        var blurb = UI.Label("Let Claude Code, Codex, Cursor and other agents search your history, see your screenshots, put things on your clipboard and design mascots.", 12, UI.Secondary);
        blurb.TextWrapping = TextWrapping.Wrap;
        blurb.Margin = new Thickness(0, 0, 0, 6);
        agents.Children.Add(blurb);
        var copy = Button("Copy setup prompt", UI.Copy, () => { });
        copy.MouseLeftButtonUp += (_, _) =>
        {
            Board.WriteText(Mcp.SetupPrompt(Process.GetCurrentProcess().MainModule!.FileName), transient: true);
            SetButton(copy, "Copied! Paste it into your agent", UI.Check);
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            reset.Tick += (_, _) => { reset.Stop(); SetButton(copy, "Copy setup prompt", UI.Copy); };
            reset.Start();
        };
        agents.Children.Add(copy);
        agents.Children.Add(Note("Paste it into your agent and it connects itself.", top: 4));

        var general = Section("General");
        general.Children.Add(Row(UI.Power, "Open at login", null, Switch(App.OpenAtLogin, on => App.OpenAtLogin = on)));
        var buttons = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Button("Clear all history…", null, ClearAll));
        var quit = Button("Quit ClipBubble", null, () => Application.Current.Shutdown());
        quit.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(quit);
        general.Children.Add(buttons);
    }

    StackPanel Section(string title)
    {
        var s = new StackPanel { Margin = new Thickness(0, content.Children.Count == 0 ? 0 : 18, 0, 0) };
        var label = UI.Tracked(title.ToUpperInvariant(), 10, 0.6, UI.Tertiary, FontWeights.SemiBold);
        label.Margin = new Thickness(0, 0, 0, 6);
        s.Children.Add(label);
        content.Children.Add(s);
        return s;
    }

    static TextBlock Note(string text, double top = 2)
    {
        var t = UI.Label(text, 10.5, UI.Tertiary);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    static FrameworkElement Row(string icon, string title, string? detail, FrameworkElement trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = UI.Icon(icon, 13, UI.Secondary);
        i.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(i);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(UI.Label(title, 13, UI.Primary, FontWeights.Medium));
        if (detail != null)
        {
            var d = UI.Label(detail, 11, UI.Secondary);
            d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(d);
        }
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trailing, 2);
        g.Children.Add(trailing);
        return g;
    }

    /// A feature that needs a folder: switch, where it looks, and whether it can read it.
    FrameworkElement PermRow(string icon, string title, string? folder, string? note, bool? ok, string pref, FrameworkElement? extra)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = UI.Icon(icon, 13, UI.Secondary);
        i.HorizontalAlignment = HorizontalAlignment.Center;
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(0, 1, 0, 0);
        g.Children.Add(i);
        var texts = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(UI.Label(title, 13, UI.Primary, FontWeights.Medium));
        if (folder != null)
        {
            var f = UI.Label(UI.Tilde(folder), 11, UI.Secondary);
            f.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            f.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(f);
        }
        if (note != null) { var n = UI.Label(note, 11, UI.Secondary); n.TextWrapping = TextWrapping.Wrap; n.Margin = new Thickness(0, 3, 0, 0); texts.Children.Add(n); }
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        void Status(string glyph, string text, Brush color)
        {
            var icon = UI.Icon(glyph, 11, color);
            icon.Margin = new Thickness(0, 0, 4, 0);
            status.Children.Add(icon);
            status.Children.Add(UI.Label(text, 11, color, FontWeights.Medium));
        }
        switch (ok)
        {
            case true: Status(UI.Ok, "Allowed", new SolidColorBrush(UI.Green)); break;
            case false: Status(UI.Warning, "Can't find this folder", new SolidColorBrush(UI.Orange)); break;
            default: Status(UI.Off, "Off", UI.Tertiary); break;
        }
        texts.Children.Add(status);
        if (extra != null) { extra.Margin = new Thickness(0, 3, 0, 0); texts.Children.Add(extra); }
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        var sw = Switch(Prefs.On(pref), on => model.SetPref(pref, on));
        sw.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(sw, 2);
        g.Children.Add(sw);
        return g;
    }

    /// The Mac's mini switch: a 26 x 15 capsule with a white knob.
    static FrameworkElement Switch(bool on, Action<bool> changed)
    {
        var knob = new Ellipse { Width = 13, Height = 13, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(on ? 12 : 1, 0, 0, 0) };
        var track = new Border { Width = 26, Height = 15, CornerRadius = new CornerRadius(7.5), Background = on ? UI.AccentBrush : UI.White(0.18), Child = knob, Cursor = Cursors.Arrow };
        track.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            track.Background = on ? UI.AccentBrush : UI.White(0.18);
            knob.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(on ? 12 : 1, 0, 0, 0), TimeSpan.FromSeconds(0.15)));
            changed(on);
        };
        return track;
    }

    static FrameworkElement Check(string title, bool on, Action<bool> changed)
    {
        var box = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1) };
        var mark = new TextBlock { Text = UI.Check, FontFamily = UI.Icons, FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Child = mark;
        void Paint() { box.Background = on ? UI.AccentBrush : UI.White(0.08); box.BorderBrush = on ? UI.AccentBrush : UI.White(0.25); mark.Visibility = on ? Visibility.Visible : Visibility.Hidden; }
        Paint();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(box);
        row.Children.Add(UI.Label(title, 11, UI.Secondary));
        row.MouseLeftButtonUp += (_, _) => { on = !on; Paint(); changed(on); };
        return row;
    }

    static FrameworkElement Link(string title, Action act, double left = 0)
    {
        var t = UI.Label(title, 11, UI.AccentBrush);
        t.Margin = new Thickness(left, 0, 0, 0);
        t.Cursor = Cursors.Hand;
        t.MouseLeftButtonUp += (_, _) => act();
        return t;
    }

    static Border Button(string title, string? icon, Action act)
    {
        var b = new Border { CornerRadius = new CornerRadius(5), Background = UI.White(0.14), Padding = new Thickness(8, 2, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Arrow };
        SetButton(b, title, icon);
        b.MouseEnter += (_, _) => b.Background = UI.White(0.2);
        b.MouseLeave += (_, _) => b.Background = UI.White(0.14);
        b.MouseLeftButtonUp += (_, _) => act();
        return b;
    }

    static void SetButton(Border b, string title, string? icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) { var i = UI.Icon(icon, 11, UI.Primary); i.Margin = new Thickness(0, 0, 5, 0); row.Children.Add(i); }
        row.Children.Add(UI.Label(title, 11, UI.Primary));
        b.Child = row;
    }

    /// Click, then press the new shortcut. Esc cancels.
    FrameworkElement HotkeyBox()
    {
        var label = UI.Label(app.HotkeyText, 11, UI.Primary, FontWeights.Medium);
        var box = new Border { CornerRadius = new CornerRadius(5), Background = UI.White(0.09), Padding = new Thickness(8, 2, 8, 3), Child = label, Focusable = true, Cursor = Cursors.Arrow, FocusVisualStyle = null };
        box.MouseLeftButtonUp += (_, _) => { label.Text = "Press keys…"; box.Focus(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (label.Text != "Press keys…") return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { label.Text = app.HotkeyText; return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None || mods == ModifierKeys.Shift) return;   // a bare key would swallow typing everywhere
            label.Text = app.SetHotkey(mods, key) ? app.HotkeyText : "Taken, try another";
        };
        return box;
    }

    void PickShotsFolder()
    {
        if (Shell.PickFolder(Win.Handle(app.PanelWindow), FolderWatcher.ScreenshotFolder, "Pick the folder ClipBubble should watch for screenshots") is not { } folder) return;
        Prefs.Set("screenshotFolder", folder);
        model.SetPref("screenshots", true);
    }

    /// Picks an image or GIF and makes it the mascot.
    void AddMascot()
    {
        var open = new OpenFileDialog { Filter = "Images|*.png;*.gif;*.jpg;*.jpeg;*.svg;*.webp", Title = "Pick an image or GIF for your mascot. Square with a transparent background looks best." };
        if (app.Modal(() => open.ShowDialog(app.PanelWindow)) != true) return;
        try
        {
            var m = Mascots.Add(Path.GetFileNameWithoutExtension(open.FileName), "image", null, new Dictionary<string, string> { ["idle"] = open.FileName });
            Mascots.SetActive(m.Id);
            model.LoadMascots();
        }
        catch (Exception e) when (e is MascotException or IOException or UnauthorizedAccessException)
        {
            app.Modal(() => MessageBox.Show(app.PanelWindow, e.Message, "ClipBubble"));
        }
    }

    void ClearAll()
    {
        var answer = app.Modal(() => MessageBox.Show(app.PanelWindow, "This removes every saved item and image. It can't be undone.", "Clear all clipboard history?", MessageBoxButton.OKCancel, MessageBoxImage.Warning));
        if (answer != MessageBoxResult.OK) return;
        model.Db.DeleteAll();
        model.Reload();
        model.ReloadRecent();
    }
}

// MARK: - App

sealed class App : Application
{
    const double IdleAlpha = 0.4, Margin = 10;
    readonly Model model = new();
    FloatingWindow bubble = null!, panel = null!, peek = null!;
    BubbleView face = null!;
    PanelView panelView = null!;
    Capture capture = null!;
    FolderWatcher? screenshots, downloads;
    HwndSource messages = null!;
    IntPtr pasteTarget;
    DispatcherTimer? pulseWork, peekWork;
    bool dragging, pressed, bubbleHover, peekHover, pulse, modal;
    (ModifierKeys Mods, Key Key) hotkey = (ModifierKeys.Control | ModifierKeys.Shift, Key.Space);

    public Window PanelWindow => panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        model.Db.Prune();
        model.Reload();
        model.ReloadRecent();

        face = new BubbleView(model);
        bubble = new FloatingWindow(64, 64, keyable: false) { Content = face, Opacity = IdleAlpha };
        face.HitArea.MouseEnter += (_, _) => SetBubbleHover(true);
        face.HitArea.MouseLeave += (_, _) => SetBubbleHover(false);
        face.HitArea.MouseLeftButtonDown += BubbleDown;
        face.HitArea.MouseMove += BubbleDrag;
        face.HitArea.MouseLeftButtonUp += BubbleUp;
        bubble.Show();
        Snap(animated: false);

        panelView = new PanelView(model, () => new SettingsView(model, this));
        panel = new FloatingWindow(420, 470, keyable: true) { Content = Glass.Wrap(panelView, 16) };
        panel.Deactivated += (_, _) => { if (!modal) HidePanel(); };
        panel.PreviewKeyDown += HandleKey;

        var peekGlass = Glass.Wrap(new PeekView(model, () => HotkeyText), 14);
        peek = new FloatingWindow(300, 100, keyable: false) { Content = peekGlass, SizeToContent = SizeToContent.Height };
        peekGlass.MouseEnter += (_, _) => { peekHover = true; UpdatePeek(); };
        peekGlass.MouseLeave += (_, _) => { peekHover = false; UpdatePeek(); };

        model.Pick = Pick;
        model.Dismiss = HidePanel;
        model.PrefsChanged = () => { SyncScreenshots(); SyncDownloads(); ImportFolders(); };

        capture = new Capture(Captured);
        SyncScreenshots();
        SyncDownloads();
        ImportFolders();
        After(8, ImportFolders);
        Every(3600, () => { model.Db.Prune(); ImportFolders(); });

        messages = new HwndSource(new HwndSourceParameters("ClipBubble messages") { ParentWindow = new IntPtr(-3) });
        messages.AddHook(Messages);
        hotkey = ParseHotkey(Prefs.Get("hotkey", "Ctrl+Shift+Space")) ?? (ModifierKeys.Control | ModifierKeys.Shift, Key.Space);
        RegisterHotkey();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => Snap(animated: false)));
        // Mascot changes from the MCP server (another process) and second launches land here.
        Listen(Mascots.ChangedEvent, () => { model.LoadMascots(); Captured(pulseOnly: true); });
        Listen(Program.OpenEvent, () => { if (!panel.IsVisible) ShowPanel(); });
    }

    void Listen(string name, Action act)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => Dispatcher.BeginInvoke(act), null, -1, executeOnlyOnce: false);
    }

    void After(double seconds, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); act(); };
        t.Start();
    }

    void Every(double seconds, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => act();
        t.Start();
    }

    IntPtr Messages(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312) Toggle();   // WM_HOTKEY
        if (msg == 0x001A && wParam == new IntPtr(0x2F)) Snap(animated: false);   // SPI_SETWORKAREA: the taskbar moved
        return IntPtr.Zero;
    }

    // MARK: Hotkey

    public string HotkeyText => string.Join("+", new[] {
        hotkey.Mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : null, hotkey.Mods.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
        hotkey.Mods.HasFlag(ModifierKeys.Shift) ? "Shift" : null, hotkey.Mods.HasFlag(ModifierKeys.Windows) ? "Win" : null,
        hotkey.Key == Key.Space ? "Space" : new KeyConverter().ConvertToString(hotkey.Key) }.Where(s => s != null));

    static (ModifierKeys, Key)? ParseHotkey(string text)
    {
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var part in text.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": mods |= ModifierKeys.Control; break;
                case "Alt": mods |= ModifierKeys.Alt; break;
                case "Shift": mods |= ModifierKeys.Shift; break;
                case "Win": mods |= ModifierKeys.Windows; break;
                default: try { key = (Key)new KeyConverter().ConvertFromString(part)!; } catch (Exception e) when (e is NotSupportedException or ArgumentException) { return null; } break;
            }
        }
        return key is { } k && mods != ModifierKeys.None ? (mods, k) : null;
    }

    bool RegisterHotkey()
    {
        uint mods = 0x4000;   // MOD_NOREPEAT
        if (hotkey.Mods.HasFlag(ModifierKeys.Alt)) mods |= 0x1;
        if (hotkey.Mods.HasFlag(ModifierKeys.Control)) mods |= 0x2;
        if (hotkey.Mods.HasFlag(ModifierKeys.Shift)) mods |= 0x4;
        if (hotkey.Mods.HasFlag(ModifierKeys.Windows)) mods |= 0x8;
        return Win.RegisterHotKey(messages.Handle, 1, mods, (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key));
    }

    /// Switches the global shortcut; keeps the old one if another app already owns the new one.
    public bool SetHotkey(ModifierKeys mods, Key key)
    {
        var old = hotkey;
        Win.UnregisterHotKey(messages.Handle, 1);
        hotkey = (mods, key);
        if (!RegisterHotkey())
        {
            hotkey = old;
            RegisterHotkey();
            return false;
        }
        Prefs.Set("hotkey", HotkeyText);
        model.ReloadRecent();
        return true;
    }

    public static bool OpenAtLogin
    {
        get => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "ClipBubble", null) is string;
        set
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run == null) return;
            if (value) run.SetValue("ClipBubble", $"\"{Process.GetCurrentProcess().MainModule!.FileName}\"");
            else run.DeleteValue("ClipBubble", throwOnMissingValue: false);
        }
    }

    /// Runs a dialog without the panel closing itself when the dialog takes focus.
    public T Modal<T>(Func<T> show)
    {
        modal = true;
        try { return show(); }
        finally { modal = false; if (panel.IsVisible) panel.Activate(); }
    }

    // MARK: Folders

    /// Every screenshot lands in history; copying it to the clipboard as well is optional.
    void SyncScreenshots()
    {
        var folder = FolderWatcher.ScreenshotFolder;
        if (!Prefs.On("screenshots")) { screenshots?.Dispose(); screenshots = null; return; }
        if (screenshots?.Folder == folder) return;
        screenshots?.Dispose();
        screenshots = new FolderWatcher(folder, 120, p => Store.IsImagePath(p) || new[] { ".mov", ".mp4" }.Contains(Path.GetExtension(p).ToLowerInvariant()), (path, date) =>
        {
            model.Db.InsertIfMissing(ClipKind.File, path, "Screenshot", "f:" + Store.Sha(path), date);
            if (Prefs.Get("copyScreenshots", true)) Board.WriteFiles(new[] { path }); else Captured();
        });
    }

    /// Watches Downloads only while that's switched on.
    void SyncDownloads()
    {
        if (Prefs.On("downloads"))
        {
            if (downloads != null || FolderWatcher.DownloadsFolder is not { } dir) return;
            downloads = new FolderWatcher(dir, null, p => !FolderWatcher.TempExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant()), (path, date) =>
            {
                model.Db.InsertIfMissing(ClipKind.File, path, "Downloads", "f:" + Store.Sha(path), date);
                Captured();
            });
        }
        else { downloads?.Dispose(); downloads = null; }
    }

    /// Backfills the last 7 days of screenshots and downloads. Cheap and idempotent, so it also reruns later.
    void ImportFolders()
    {
        foreach (var (watcher, source) in new[] { (screenshots, "Screenshot"), (downloads, "Downloads") })
            if (watcher != null)
                foreach (var (path, date) in watcher.Existing(7))
                    model.Db.InsertIfMissing(ClipKind.File, path, source, "f:" + Store.Sha(path), date);
        model.Reload();
        model.ReloadRecent();
    }

    // MARK: Picking

    /// Puts the clip on the clipboard and, unless copying only, pastes it into the app underneath.
    void Pick(Clip c, bool paste)
    {
        var target = panel.IsVisible ? pasteTarget : Win.GetForegroundWindow();
        Board.Write(c);
        HidePanel();
        HidePeek();
        if (!paste || !Prefs.Get("autoPaste", true) || target == IntPtr.Zero || !Win.IsWindow(target) || !Win.CanPasteInto(target)) return;
        Win.SetForegroundWindow(target);
        // Wait until the app is back in front and the keys used to pick (Enter, Ctrl, Alt) are released.
        var started = DateTime.Now;
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        wait.Tick += (_, _) =>
        {
            var ready = Win.GetForegroundWindow() == target && !Win.ModifiersDown();
            if (!ready && (DateTime.Now - started).TotalSeconds < 1.5) return;
            wait.Stop();
            if (ready) Win.SendPaste();
        };
        wait.Start();
    }

    void HandleKey(object sender, KeyEventArgs e)
    {
        if (model.ShowSettings)
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not Border) { model.ShowSettings = false; e.Handled = true; }
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Down: model.Move(1); break;
            case Key.Up: model.Move(-1); break;
            case Key.Enter: if (model.Selected is { } c) Pick(c, !alt); break;
            case Key.Tab: model.CycleFilter(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); break;
            case Key.Escape:
                if (model.Previewing) model.Previewing = false;
                else if (model.Query.Length > 0) model.Query = "";
                else HidePanel();
                break;
            case Key.Back when ctrl: if (model.Selected is { } d) model.Delete(d); break;
            case Key.Y when ctrl: model.Previewing = !model.Previewing; break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                var n = key - Key.D1;
                if (n < model.Clips.Count) Pick(model.Clips[n], !alt);
                break;
            default: e.Handled = false; break;
        }
    }

    // MARK: Corner magnet

    /// Moves the bubble to the nearest screen corner (or the saved one when not dragging), with a little overshoot.
    void Snap(bool animated)
    {
        var f = Win.Frame(bubble);
        var center = new Win.POINT { X = (f.Left + f.Right) / 2, Y = (f.Top + f.Bottom) / 2 };
        if (!animated && f.Left < -5000) center = new Win.POINT();   // first placement: the primary screen
        var area = Win.WorkArea(center);
        var corner = Prefs.Get("corner", "br");
        if (animated)
        {
            corner = (center.Y < (area.Top + area.Bottom) / 2 ? "t" : "b") + (center.X > (area.Left + area.Right) / 2 ? "r" : "l");
            Prefs.Set("corner", corner);
        }
        var scale = Win.Scale(bubble);
        int w = f.Right - f.Left, h = f.Bottom - f.Top, m = (int)Math.Round(Margin * scale);
        var x = corner.EndsWith("l") ? area.Left + m : area.Right - w - m;
        var y = corner.StartsWith("t") ? area.Top + m : area.Bottom - h - m;
        if (!animated) { Win.Move(bubble, x, y); return; }
        Animate(bubble, f.Left, f.Top, x, y, 0.32, RestBubble);
    }

    /// Moves a window along cubic-bezier(0.3, 1.35, 0.5, 1), the Mac's overshoot curve.
    static void Animate(Window w, int x0, int y0, int x1, int y1, double seconds, Action done)
    {
        var start = DateTime.Now;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var t = Math.Min(1, (DateTime.Now - start).TotalSeconds / seconds);
            var k = Bezier(t, 0.3, 1.35, 0.5, 1);
            Win.Move(w, (int)Math.Round(x0 + (x1 - x0) * k), (int)Math.Round(y0 + (y1 - y0) * k));
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            done();
        };
        CompositionTarget.Rendering += tick;
    }

    static double Bezier(double t, double x1, double y1, double x2, double y2)
    {
        double X(double s) => 3 * (1 - s) * (1 - s) * s * x1 + 3 * (1 - s) * s * s * x2 + s * s * s;
        double Y(double s) => 3 * (1 - s) * (1 - s) * s * y1 + 3 * (1 - s) * s * s * y2 + s * s * s;
        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++) { var mid = (lo + hi) / 2; if (X(mid) < t) lo = mid; else hi = mid; }
        return Y((lo + hi) / 2);
    }

    // MARK: Bubble mouse

    Win.POINT downAt;
    Win.RECT startFrame;
    bool moved;

    void BubbleDown(object sender, MouseButtonEventArgs e)
    {
        downAt = Win.Cursor();
        startFrame = Win.Frame(bubble);
        moved = false;
        pressed = true;
        face.HitArea.CaptureMouse();
        RefreshMood();
    }

    void BubbleDrag(object sender, MouseEventArgs e)
    {
        if (!pressed) return;
        var p = Win.Cursor();
        if (!moved)
        {
            if (Math.Sqrt(Math.Pow(p.X - downAt.X, 2) + Math.Pow(p.Y - downAt.Y, 2)) <= 3 * Win.Scale(bubble)) return;
            moved = true;
            dragging = true;
            HidePanel();
            HidePeek();
            SetAlpha(1);
        }
        Win.Move(bubble, startFrame.Left + p.X - downAt.X, startFrame.Top + p.Y - downAt.Y);
    }

    void BubbleUp(object sender, MouseButtonEventArgs e)
    {
        face.HitArea.ReleaseMouseCapture();
        if (!pressed) return;
        pressed = false;
        RefreshMood();
        if (moved) { dragging = false; Snap(animated: true); }
        else Toggle();
    }

    void SetBubbleHover(bool on)
    {
        if (on && !bubbleHover) face.Wiggle();
        bubbleHover = on;
        RefreshMood();
        UpdatePeek();
    }

    void RefreshMood() => face.SetMood(bubbleHover, pressed, pulse);

    void SetAlpha(double to) => UI.Fade(bubble, to, 0.25);

    /// The mascot hops and the bubble flashes opaque whenever something is captured.
    void Captured() => Captured(pulseOnly: false);

    void Captured(bool pulseOnly)
    {
        if (!pulseOnly)
        {
            model.ReloadRecent();
            if (panel.IsVisible) model.Reload(); else model.Unseen++;
        }
        pulse = true;
        RefreshMood();
        face.Pulse();
        SetAlpha(1);
        pulseWork?.Stop();
        pulseWork = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        pulseWork.Tick += (_, _) => { pulseWork.Stop(); pulse = false; RefreshMood(); RestBubble(); };
        pulseWork.Start();
    }

    void RestBubble()
    {
        if (!panel.IsVisible && !peek.IsVisible && !bubbleHover && !pulse) SetAlpha(IdleAlpha);
    }

    // MARK: Panel

    void Toggle() { if (panel.IsVisible) HidePanel(); else ShowPanel(); }

    /// Opens the panel anchored to the bubble's corner, growing toward the screen center.
    void ShowPanel()
    {
        HidePeek();
        var front = Win.GetForegroundWindow();
        if (front != Win.Handle(panel)) pasteTarget = front;
        model.LoadMascots();
        model.Query = "";
        model.Filter = "all";
        model.Previewing = false;
        model.ShowSettings = false;
        model.Reload();
        model.Selection = model.Clips.FirstOrDefault()?.Id;
        model.Unseen = 0;

        panel.Opacity = 0;
        panel.Show();
        var b = Win.Frame(bubble);
        var p = Win.Frame(panel);
        var area = Win.WorkArea(new Win.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 });
        var s = Win.Scale(bubble);
        int pw = p.Right - p.Left, ph = p.Bottom - p.Top, edge = (int)(8 * s), inset = (int)(9 * s), lap = (int)(4 * s);
        var x = (b.Left + b.Right) / 2 > (area.Left + area.Right) / 2 ? b.Right - pw - inset : b.Left + inset;
        var y = (b.Top + b.Bottom) / 2 < (area.Top + area.Bottom) / 2 ? b.Bottom - lap : b.Top - ph + lap;
        Win.Move(panel, Math.Min(Math.Max(x, area.Left + edge), area.Right - pw - edge), Math.Min(Math.Max(y, area.Top + edge), area.Bottom - ph - edge));
        panel.Activate();
        model.Shown();
        UI.Fade(panel, 1, 0.1);
        SetAlpha(1);
    }

    /// Instant, so a paste lands without waiting on an animation.
    void HidePanel()
    {
        if (!panel.IsVisible) return;
        panel.Hide();
        RestBubble();
    }

    // MARK: Peek

    void UpdatePeek()
    {
        peekWork?.Stop();
        var want = (bubbleHover || peekHover) && !panel.IsVisible && !dragging;
        if (want && peek.IsVisible) return;
        peekWork = new DispatcherTimer { Interval = TimeSpan.FromSeconds(want ? 0.3 : 0.2) };
        peekWork.Tick += (_, _) => { peekWork.Stop(); if (want) ShowPeek(); else HidePeek(); };
        peekWork.Start();
        if (want) SetAlpha(1); else RestBubble();
    }

    void PlacePeek()
    {
        var b = Win.Frame(bubble);
        var p = Win.Frame(peek);
        var area = Win.WorkArea(new Win.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 });
        var s = Win.Scale(bubble);
        int pw = p.Right - p.Left, ph = p.Bottom - p.Top, edge = (int)(8 * s), gap = (int)(4 * s), inset = (int)(9 * s);
        var x = (b.Left + b.Right) / 2 > (area.Left + area.Right) / 2 ? b.Left - pw - gap : b.Right + gap;
        var y = (b.Top + b.Bottom) / 2 < (area.Top + area.Bottom) / 2 ? b.Top + inset : b.Bottom - inset - ph;
        Win.Move(peek, Math.Min(Math.Max(x, area.Left + edge), area.Right - pw - edge), Math.Min(Math.Max(y, area.Top + edge), area.Bottom - ph - edge));
    }

    void ShowPeek()
    {
        // Re-check at show time: a click may have opened the panel while this was scheduled.
        if (peek.IsVisible || panel.IsVisible || dragging || !(bubbleHover || peekHover)) return;
        model.ReloadRecent();
        model.Shown();
        model.Unseen = 0;
        peek.Opacity = 0;
        peek.Show();
        peek.UpdateLayout();
        PlacePeek();
        UI.Fade(peek, 1, 0.12);
    }

    void HidePeek()
    {
        peekWork?.Stop();
        peekHover = false;
        if (!peek.IsVisible) return;
        peek.Hide();
        RestBubble();
    }
}
