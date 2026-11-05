using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SessionStartupManager;

// Every shortcut gets its own AppUserModelID. Without one, the taskbar treats all shortcuts
// to the same exe as one app and only lets a single one be pinned.
internal static class Shortcuts
{
    public const string ManagerAppId = "SessionStartupManager.Manager";
    const string LaunchAllAppId = "SessionStartupManager.LaunchAll";
    const string LaunchAllName = "Launch Sessions.lnk";
    const string ManagerName = "Session Startup Manager.lnk";

    static string Programs => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    static string GroupDir => Path.Combine(Programs, "Session Startup Manager");
    static string Exe => Environment.ProcessPath;

    // Returns the desktop shortcut to pin.
    public static string CreateLaunchAll()
    {
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LaunchAllName);
        const string desc = "Launch every group ticked for Launch all";
        Create(desktop, "--launch", desc, LaunchAllAppId);
        Create(Path.Combine(Programs, LaunchAllName), "--launch", desc, LaunchAllAppId);
        Create(Path.Combine(Programs, ManagerName), "", "Configure which windows get launched", ManagerAppId);
        return desktop;
    }

    // Returns the group's Start menu shortcut to pin.
    public static string CreateGroup(LaunchGroup g)
    {
        Directory.CreateDirectory(GroupDir);
        string path = Path.Combine(GroupDir, "Launch " + SafeFileName(g.Name) + ".lnk");
        RemoveGroup(g, except: path); // drop the shortcut from before a rename
        Create(path, "--group " + g.Id, $"Launch the \"{g.Name}\" group", "SessionStartupManager.Group." + g.Id);
        return path;
    }

    public static void RemoveGroup(LaunchGroup g, string except = null)
    {
        if (!Directory.Exists(GroupDir)) return;
        foreach (var lnk in Directory.GetFiles(GroupDir, "*.lnk"))
        {
            if (string.Equals(lnk, except, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (ReadArguments(lnk).Contains("--group " + g.Id, StringComparison.OrdinalIgnoreCase)) File.Delete(lnk); }
            catch { }
        }
    }

    static void Create(string lnk, string args, string description, string appId)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(Exe);
            link.SetArguments(args);
            link.SetWorkingDirectory(Path.GetDirectoryName(Exe));
            link.SetDescription(description);
            link.SetIconLocation(Exe, 0);

            var store = (IPropertyStore)link;
            var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // System.AppUserModel.ID
            var value = PropVariant.FromString(appId);
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally { value.Free(); }

            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    static string ReadArguments(string lnk)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetArguments(sb, sb.Capacity);
            return sb.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '-' : c).ToArray()).Trim();
        return s.Length == 0 ? "group" : s;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;

        public static PropVariant FromString(string s) => new() { vt = 31 /* VT_LPWSTR */, ptr = Marshal.StringToCoTaskMemUni(s) };

        public void Free()
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            ptr = IntPtr.Zero;
        }
    }
}
