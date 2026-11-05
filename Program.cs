namespace SessionStartupManager;

internal static class Program
{
    // (no args)          open the manager
    // --launch           launch every group ticked for Launch all
    // --group <id|name>  launch one group (repeatable)
    // --startup          launch the Run-at-startup groups after the startup delay
    // Holding Shift on --launch/--group opens the manager instead.
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var cfg = AppConfig.Load(out string loadError);
        if (loadError != null)
            MessageBox.Show(loadError, "Session Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        var groupKeys = args.Select((a, i) => (a, i))
            .Where(x => x.a.Equals("--group", StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length)
            .Select(x => args[x.i + 1])
            .ToList();

        if (Has("--startup"))
        {
            WaitForShell(cfg.StartupDelaySec);
            Run(cfg, cfg.Groups.Where(g => g.RunAtStartup));
            return;
        }

        if ((Has("--launch") || groupKeys.Count > 0) && !Native.ShiftHeld())
        {
            var groups = Has("--launch") ? cfg.Groups.Where(g => g.IncludeInLaunchAll).ToList() : new List<LaunchGroup>();
            var missing = new List<string>();
            foreach (var key in groupKeys)
            {
                var g = cfg.FindGroup(key);
                if (g == null) missing.Add(key);
                else if (!groups.Contains(g)) groups.Add(g);
            }
            if (missing.Count > 0)
                Warn("No group named " + string.Join(", ", missing) + ". It may have been deleted.");
            if (Run(cfg, groups)) return;
            // Nothing to launch: fall through to the manager so the user can fix that.
        }

        Native.SetCurrentProcessExplicitAppUserModelID(Shortcuts.ManagerAppId);
        Application.Run(new MainForm(cfg));
    }

    // Returns false when there was nothing enabled to launch.
    static bool Run(AppConfig cfg, IEnumerable<LaunchGroup> groups)
    {
        var jobs = groups
            .Select(g => new LaunchJob { Group = g, Entries = g.Entries.Where(e => e.Enabled).ToList() })
            .Where(j => j.Entries.Count > 0)
            .ToList();
        if (jobs.Count == 0) return false;

        var errors = Launcher.Launch(cfg, jobs);
        if (errors.Count > 0) Warn("Some windows didn't start or couldn't be placed:\n\n" + string.Join("\n", errors));
        return true;
    }

    // At logon the taskbar and virtual desktop state come up after Run entries fire.
    static void WaitForShell(int delaySec)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(500);
        Thread.Sleep(Math.Max(0, delaySec) * 1000);
    }

    static void Warn(string text) =>
        MessageBox.Show(text, "Session Startup Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

internal static class AppIcon
{
    static Icon _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        try { _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
        catch { _icon = SystemIcons.Application; }
        return _icon;
    }
}
