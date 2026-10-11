using System.Diagnostics;
using System.Text;

namespace ClaudeStartupManager;

internal sealed class LaunchJob
{
    public LaunchGroup Group;
    public List<LaunchEntry> Entries;
}

internal static class Launcher
{
    sealed class Plan
    {
        public string Title, Dir, Exe, Args;
        public bool Lock;
    }

    const int FirstWindowTimeoutMs = 15000; // a cold Windows Terminal start can take a few seconds
    const int NextWindowTimeoutMs = 8000;

    // Runs each group in order: switch desktop, open its windows, arrange them on its monitor.
    // Returns one line per problem.
    public static List<string> Launch(AppConfig cfg, IReadOnlyList<LaunchJob> jobs, IProgress<string> progress = null)
    {
        var errors = new List<string>();
        bool wt = cfg.UseWindowsTerminal && OnPath("wt.exe");
        if (wt) TerminalState.ScrubLauncherTabs();
        int startDesktop = jobs.Any(j => j.Group.Desktop > 0) ? VirtualDesktops.CurrentIndex() : 0;
        bool switched = false;

        foreach (var job in jobs)
        {
            var g = job.Group;
            var plans = new List<Plan>();
            foreach (var e in job.Entries)
            {
                try { plans.Add(BuildPlan(cfg, e)); }
                catch (Exception ex) { errors.Add($"{g.Name} / {Label(e)}: {ex.Message}"); }
            }
            if (plans.Count == 0) continue;

            if (g.Desktop > 0)
            {
                progress?.Report($"{g.Name}: switching to desktop {g.Desktop}…");
                try { VirtualDesktops.SwitchTo(g.Desktop); switched = true; }
                catch (Exception ex) { errors.Add($"{g.Name}: couldn't switch desktop ({ex.Message}), opened on the current one"); }
            }

            progress?.Report($"{g.Name}: opening {plans.Count} window(s)…");
            var seen = TerminalWindows.Snapshot();
            var windows = new List<IntPtr>();
            int expected;

            if (wt && g.TabsInOneWindow)
            {
                expected = 1;
                try
                {
                    Start("wt.exe", "-w new " + string.Join(" ; ", plans.Select(WtTab)), null);
                    Collect(seen, windows, FirstWindowTimeoutMs, plans.Select(p => p.Title).ToList());
                }
                catch (Exception ex) { errors.Add($"{g.Name}: Windows Terminal: {ex.Message}"); }
            }
            else
            {
                expected = plans.Count;
                for (int i = 0; i < plans.Count; i++)
                {
                    var p = plans[i];
                    try
                    {
                        if (wt) Start("wt.exe", "-w new " + WtTab(p), null);
                        else Start(p.Exe, p.Args, p.Dir);
                        // Waiting for each window keeps them in list order and makes sure they
                        // exist before we move on to another desktop.
                        Collect(seen, windows, i == 0 ? FirstWindowTimeoutMs : NextWindowTimeoutMs, new[] { p.Title });
                    }
                    catch (Exception ex) { errors.Add($"{g.Name} / {p.Title}: {ex.Message}"); }
                    if (i < plans.Count - 1 && cfg.DelayMs > 0) Thread.Sleep(cfg.DelayMs);
                }
            }

            if (!string.IsNullOrEmpty(g.Monitor))
            {
                var monitor = Monitors.Find(g.Monitor, g.MonitorBounds);
                if (monitor == null)
                    errors.Add($"{g.Name}: monitor {g.Monitor} isn't connected, windows left where they opened");
                else
                {
                    Thread.Sleep(300); // let Terminal finish its own initial sizing first
                    WindowPlacer.Arrange(windows, monitor.WorkingArea, g.Arrange);
                }
            }

            if (windows.Count < expected && (g.Desktop > 0 || !string.IsNullOrEmpty(g.Monitor)))
                errors.Add($"{g.Name}: saw {windows.Count} of {expected} window(s) appear, the rest may not be placed");
        }

        if (switched && cfg.ReturnToStartingDesktop && startDesktop > 0)
        {
            progress?.Report("Returning to the starting desktop…");
            try { VirtualDesktops.SwitchTo(startDesktop); }
            catch (Exception ex) { errors.Add("Couldn't switch back: " + ex.Message); }
        }
        return errors;
    }

    static void Collect(HashSet<IntPtr> seen, List<IntPtr> found, int timeoutMs, ICollection<string> titles)
    {
        var h = TerminalWindows.WaitForNew(seen, timeoutMs, titles);
        if (h == IntPtr.Zero) return;
        seen.Add(h);
        found.Add(h);
    }

    static Plan BuildPlan(AppConfig cfg, LaunchEntry e)
    {
        bool linuxDir = e.Kind == EntryKind.Wsl && IsLinuxPath(e.WorkingDirectory);
        string dir = linuxDir ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : ResolveDir(e.WorkingDirectory);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("folder not found: " + dir);

        string title = Clean(e.Kind == EntryKind.Claude ? ClaudeTitle(e, dir) ?? e.Name : e.Name);
        if (title.Length == 0) title = e.Kind == EntryKind.Wsl && !string.IsNullOrWhiteSpace(e.Distro) ? e.Distro : LaunchEntry.KindText(e.Kind);

        var p = new Plan { Title = title, Dir = dir, Lock = e.LockTitle };
        switch (e.Kind)
        {
            case EntryKind.Claude:
                p.Exe = ClaudeHost(cfg);
                p.Args = PsArgs(PsTitle(title) + "\n" + ClaudeCommand(e));
                break;
            case EntryKind.Pwsh:
                p.Exe = "pwsh.exe";
                p.Args = PsArgs(PsTitle(title) + "\n" + (e.Arguments ?? ""));
                break;
            case EntryKind.WindowsPowerShell:
                p.Exe = "powershell.exe";
                p.Args = PsArgs(PsTitle(title) + "\n" + (e.Arguments ?? ""));
                break;
            case EntryKind.Cmd:
                p.Exe = "cmd.exe";
                p.Args = "/k title " + CmdEscape(title)
                    + (string.IsNullOrWhiteSpace(e.Arguments) ? "" : " & " + e.Arguments.Trim());
                break;
            case EntryKind.Wsl:
                p.Exe = "wsl.exe";
                p.Args = WslArgs(e, linuxDir ? e.WorkingDirectory.Trim() : dir);
                break;
        }
        return p;
    }

    // wsl.exe -d <distro> --cd <dir> [-e bash -ic "source <(echo <base64> | base64 -d); exec bash"]
    // wsl.exe doesn't honor \" escapes, so the user's command travels base64-encoded and bash
    // sources it. Sourcing (not bash -c) keeps aliases/functions from .bashrc and any cd it does.
    static string WslArgs(LaunchEntry e, string cd)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(e.Distro)) sb.Append("-d ").Append(ArgvQuote(e.Distro.Trim())).Append(' ');
        sb.Append("--cd ").Append(ArgvQuote(cd));
        if (!string.IsNullOrWhiteSpace(e.Arguments))
        {
            string script = e.Arguments.Trim().Replace("\r\n", "\n") + "\n";
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            sb.Append(" -e bash -ic \"source <(echo ").Append(b64).Append(" | base64 -d); exec bash\"");
        }
        return sb.ToString();
    }

    static bool IsLinuxPath(string d)
    {
        d = d?.Trim() ?? "";
        return d.StartsWith('/') || d == "~" || d.StartsWith("~/");
    }

    // The /rename name Claude stored for the session, falling back to Claude's auto title.
    static string ClaudeTitle(LaunchEntry e, string dir) => e.ClaudeMode switch
    {
        ClaudeMode.Resume when !string.IsNullOrWhiteSpace(e.SessionId) => SessionScanner.TitleForSession(e.SessionId.Trim()),
        ClaudeMode.Continue => SessionScanner.TitleForLatestInFolder(dir),
        _ => null,
    };

    static string ClaudeCommand(LaunchEntry e)
    {
        var sb = new StringBuilder("claude");
        switch (e.ClaudeMode)
        {
            case ClaudeMode.Resume:
                sb.Append(" --resume");
                if (!string.IsNullOrWhiteSpace(e.SessionId)) sb.Append(' ').Append(PsQuote(e.SessionId.Trim()));
                break;
            case ClaudeMode.Continue:
                sb.Append(" --continue");
                break;
            case ClaudeMode.New:
                if (!string.IsNullOrWhiteSpace(e.Name)) sb.Append(" --name ").Append(PsQuote(e.Name.Trim()));
                break;
        }
        if (!string.IsNullOrWhiteSpace(e.Arguments)) sb.Append(' ').Append(e.Arguments.Trim());
        return sb.ToString();
    }

    static string WtTab(Plan p) =>
        "new-tab --title " + WtQuote(p.Title)
        + (p.Lock ? " --suppressApplicationTitle" : "")
        + " -d " + WtQuote(p.Dir)
        + " " + p.Exe + " " + p.Args.Replace(";", "\\;"); // a bare ; splits wt subcommands

    static void Start(string exe, string args, string dir)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        if (dir != null) psi.WorkingDirectory = dir;
        Process.Start(psi)?.Dispose();
    }

    // Encoded commands skip every quoting layer between here and PowerShell.
    static string PsArgs(string script) =>
        "-NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    static string PsTitle(string title) => "$Host.UI.RawUI.WindowTitle = " + PsQuote(title);

    static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    // CommandLineToArgvW rules: backslashes only matter right before a quote.
    static string ArgvQuote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1); slashes = 0; sb.Append('"'); continue; }
            sb.Append('\\', slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    static string CmdEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if ("^&|<>()".IndexOf(c) >= 0) sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string WtQuote(string s) => "\"" + (s.EndsWith('\\') ? s + "\\" : s) + "\"";

    static string Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s.Trim())
        {
            if (char.IsControl(c)) continue;
            sb.Append(c switch { '"' => '\'', ';' => ',', _ => c });
        }
        return sb.ToString();
    }

    static string ResolveDir(string d)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(d)) return home;
        d = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
        if (d == "~" || d.StartsWith("~\\") || d.StartsWith("~/")) d = home + d.Substring(1);
        return d;
    }

    static string ClaudeHost(AppConfig cfg) =>
        string.Equals(cfg.ClaudeHost, "powershell", StringComparison.OrdinalIgnoreCase) || !OnPath("pwsh.exe")
            ? "powershell.exe"
            : "pwsh.exe";

    public static bool OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), exe))) return true; } catch { }
        }
        return false;
    }

    // Installed distros, minus Docker Desktop's internal ones nobody opens a shell in.
    public static List<string> WslDistros()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode,
            };
            using var proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Split('\n')
                .Select(l => l.Trim().Trim('\0'))
                .Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return new(); }
    }

    static string Label(LaunchEntry e) => string.IsNullOrWhiteSpace(e.Name) ? LaunchEntry.KindText(e.Kind) : e.Name;
}
