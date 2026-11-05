using System.Text;

namespace SessionStartupManager;

internal static class Monitors
{
    public sealed record Info(string DeviceName, Rectangle Bounds, Rectangle WorkingArea, bool Primary, string Label);

    // Left to right, labelled by position since \\.\DISPLAYn numbers say nothing about layout.
    public static List<Info> List()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        string[] positions = screens.Count switch
        {
            2 => new[] { "left", "right" },
            3 => new[] { "left", "middle", "right" },
            _ => null,
        };
        return screens.Select((s, i) =>
        {
            string pos = positions != null ? " " + positions[i] : "";
            string label = $"{i + 1}:{pos} {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (primary)" : "")}  [{s.DeviceName.TrimStart('\\', '.')}]";
            return new Info(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary, label);
        }).ToList();
    }

    public static Info Find(string deviceName, string boundsHint)
    {
        var all = List();
        return all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => FormatBounds(m.Bounds) == boundsHint);
    }

    public static string FormatBounds(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";
}

internal static class TerminalWindows
{
    // Windows Terminal windows, plus classic console windows when WT is off.
    static readonly string[] Classes = { "CASCADIA_HOSTING_WINDOW_CLASS", "ConsoleWindowClass" };

    public static HashSet<IntPtr> Snapshot() => new(Enumerate());

    // Prefers a new window whose title contains one of the expected titles. Something else can
    // open at the same moment (e.g. Terminal restoring a saved window), so a window that doesn't
    // match is only taken once it has been the sole candidate for a moment; programs like
    // Session CLIs may retitle their window almost immediately.
    public static IntPtr WaitForNew(HashSet<IntPtr> seen, int timeoutMs, ICollection<string> expectedTitles)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr fallback = IntPtr.Zero;
        long fallbackSince = 0;
        var title = new StringBuilder(512);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var h in Enumerate())
            {
                if (seen.Contains(h) || !Native.IsWindowVisible(h)) continue;
                title.Clear();
                Native.GetWindowText(h, title, title.Capacity);
                string t = title.ToString();
                if (expectedTitles.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase))) return h;
                if (fallback == IntPtr.Zero) { fallback = h; fallbackSince = sw.ElapsedMilliseconds; }
            }
            if (fallback != IntPtr.Zero && sw.ElapsedMilliseconds - fallbackSince > 1500) return fallback;
            Thread.Sleep(100);
        }
        return fallback;
    }

    static List<IntPtr> Enumerate()
    {
        var found = new List<IntPtr>();
        var cls = new StringBuilder(256);
        Native.EnumWindows((h, _) =>
        {
            cls.Clear();
            Native.GetClassName(h, cls, cls.Capacity);
            if (Array.IndexOf(Classes, cls.ToString()) >= 0) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

internal static class WindowPlacer
{
    public static void Arrange(IReadOnlyList<IntPtr> windows, Rectangle work, ArrangeMode mode)
    {
        if (windows.Count == 0) return;
        switch (mode)
        {
            case ArrangeMode.Tile:
            {
                bool portrait = work.Height > work.Width;
                int across = (int)Math.Floor(Math.Sqrt(windows.Count));
                int along = (int)Math.Ceiling(windows.Count / (double)across);
                int cols = portrait ? across : along, rows = portrait ? along : across;
                for (int i = 0; i < windows.Count; i++)
                {
                    int c = i % cols, r = i / cols;
                    int x0 = work.X + work.Width * c / cols, x1 = work.X + work.Width * (c + 1) / cols;
                    int y0 = work.Y + work.Height * r / rows, y1 = work.Y + work.Height * (r + 1) / rows;
                    Place(windows[i], new Rectangle(x0, y0, x1 - x0, y1 - y0));
                }
                break;
            }
            case ArrangeMode.Maximize:
                foreach (var h in windows)
                {
                    Place(h, Inset(work, 0.8));
                    Native.ShowWindow(h, Native.SW_MAXIMIZE);
                }
                break;
            case ArrangeMode.Cascade:
            {
                var size = Inset(work, 0.7).Size;
                int step = 36;
                for (int i = 0; i < windows.Count; i++)
                {
                    int offset = step * (i % 8);
                    Place(windows[i], new Rectangle(work.X + 24 + offset, work.Y + 24 + offset, size.Width, size.Height));
                }
                break;
            }
            case ArrangeMode.KeepSize:
                for (int i = 0; i < windows.Count; i++)
                {
                    Native.GetWindowRect(windows[i], out var r);
                    int w = Math.Min(r.Right - r.Left, work.Width), hgt = Math.Min(r.Bottom - r.Top, work.Height);
                    int offset = 30 * (i % 8);
                    int x = Math.Min(work.X + (work.Width - w) / 2 + offset, work.Right - w);
                    int y = Math.Min(work.Y + (work.Height - hgt) / 2 + offset, work.Bottom - hgt);
                    Place(windows[i], new Rectangle(x, y, w, hgt));
                }
                break;
        }
    }

    static Rectangle Inset(Rectangle r, double scale)
    {
        int w = (int)(r.Width * scale), h = (int)(r.Height * scale);
        return new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    // Places the *visible* frame at target. Windows 10/11 frames carry invisible resize borders,
    // so the raw window rect is grown by the gap DWM reports. Done twice because crossing onto a
    // monitor with a different DPI rescales the window after the first move.
    static void Place(IntPtr h, Rectangle target)
    {
        if (Native.IsZoomed(h) || Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        for (int pass = 0; pass < 2; pass++)
        {
            int l = 0, t = 0, r = 0, b = 0;
            if (Native.GetWindowRect(h, out var wr)
                && Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var fr, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0)
            {
                l = fr.Left - wr.Left; t = fr.Top - wr.Top; r = wr.Right - fr.Right; b = wr.Bottom - fr.Bottom;
            }
            Native.SetWindowPos(h, IntPtr.Zero, target.X - l, target.Y - t, target.Width + l + r, target.Height + t + b,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }
}
