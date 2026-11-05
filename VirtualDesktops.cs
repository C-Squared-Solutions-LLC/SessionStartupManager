using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SessionStartupManager;

// Windows only lets a process move its *own* windows between virtual desktops, so instead
// of moving terminals after the fact we switch to the target desktop first (Ctrl+Win+Arrow,
// same as the user would) and open the windows there. Explorer mirrors desktop order, names
// and the active desktop into the registry, which is how switches are verified.
internal static class VirtualDesktops
{
    public sealed record Desktop(int Index, Guid Id, string Name);

    const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    const ushort VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44;

    public static List<Desktop> List()
    {
        var result = new List<Desktop>();
        using var k = Registry.CurrentUser.OpenSubKey(RegKey);
        if (k?.GetValue("VirtualDesktopIDs") is not byte[] ids) return result;
        for (int i = 0; i + 16 <= ids.Length; i += 16)
        {
            var id = new Guid(ids.AsSpan(i, 16));
            string name = null;
            using (var dk = k.OpenSubKey(@"Desktops\" + id.ToString("B")))
                name = dk?.GetValue("Name") as string;
            int index = i / 16 + 1;
            result.Add(new Desktop(index, id, string.IsNullOrWhiteSpace(name) ? "Desktop " + index : name));
        }
        return result;
    }

    public static Guid? Current()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
        {
            if (k?.GetValue("CurrentVirtualDesktop") is byte[] cur && cur.Length == 16) return new Guid(cur);
        }
        // Windows 10 keeps it per logon session.
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
            if (k?.GetValue("CurrentVirtualDesktop") is byte[] cur && cur.Length == 16) return new Guid(cur);
        }
        catch { }
        return null;
    }

    // 1-based, 0 when it can't be determined (single desktop that was never switched, etc.).
    public static int CurrentIndex()
    {
        var cur = Current();
        var list = List();
        if (cur == null) return list.Count <= 1 ? 1 : 0;
        return list.FindIndex(d => d.Id == cur.Value) + 1;
    }

    public static void SwitchTo(int target)
    {
        if (target < 1) return;
        int count = List().Count;
        int cur = CurrentIndex();
        if (cur == target) return;

        // Explorer handles Ctrl+Win+Arrow; if it's hung the keys vanish and we'd just time out.
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        IntPtr progman = Native.FindWindow("Progman", null);
        if ((tray != IntPtr.Zero && Native.IsHungAppWindow(tray)) || (progman != IntPtr.Zero && Native.IsHungAppWindow(progman)))
            throw new InvalidOperationException("Explorer isn't responding (restart it from Task Manager)");

        if (target > Math.Max(count, 1))
        {
            // Ctrl+Win+D appends a desktop and jumps to it.
            for (int n = Math.Max(count, 1); n < target; n++)
            {
                int expected = n + 1;
                Press(VK_D, extended: false);
                if (!WaitUntil(() => List().Count >= expected, 3000))
                    throw new InvalidOperationException($"couldn't create desktop {expected}");
            }
        }
        else
        {
            if (cur == 0) throw new InvalidOperationException("can't tell which desktop is active");
            ushort arrow = target > cur ? VK_RIGHT : VK_LEFT;
            for (int i = 0; i < Math.Abs(target - cur); i++)
            {
                int before = CurrentIndex();
                Press(arrow, extended: true);
                if (!WaitUntil(() => CurrentIndex() != before, 2000))
                    throw new InvalidOperationException("Windows didn't respond to Ctrl+Win+Arrow");
            }
        }

        if (!WaitUntil(() => CurrentIndex() == target, 2000))
            throw new InvalidOperationException($"ended up on desktop {CurrentIndex()} instead of {target}");

        // Let the slide animation finish; windows created mid-transition can land on the old desktop.
        Thread.Sleep(400);
    }

    static void Press(ushort vk, bool extended)
    {
        uint ext = extended ? Native.KEYEVENTF_EXTENDEDKEY : 0;
        var inputs = new[]
        {
            Key(VK_CONTROL, 0),
            Key(VK_LWIN, Native.KEYEVENTF_EXTENDEDKEY),
            Key(vk, ext),
            Key(vk, ext | Native.KEYEVENTF_KEYUP),
            Key(VK_LWIN, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP),
            Key(VK_CONTROL, Native.KEYEVENTF_KEYUP),
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    static Native.INPUT Key(ushort vk, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
