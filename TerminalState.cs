using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeStartupManager;

// With "firstWindowPreference": "persistedWindowLayout", Windows Terminal reopens every window
// that was open when it last closed. A cold start from the launcher would then bring back old
// launcher tabs (including entries since removed from a group) next to the fresh ones, and
// resume the same Claude session twice. Before a cold start, drop the launcher's tabs from the
// saved layout. Tabs the user opened by hand are kept.
internal static class TerminalState
{
    static readonly Regex EncodedPs = new(@"-NoLogo -NoExit -EncodedCommand (\S+)", RegexOptions.Compiled);

    public static void ScrubLauncherTabs()
    {
        if (Process.GetProcessesByName("WindowsTerminal").Length > 0) return; // layout only restores on a cold start
        foreach (var file in StateFiles())
        {
            try { Scrub(file); } catch { /* never block a launch over this */ }
        }
    }

    static IEnumerable<string> StateFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var dir in Directory.GetDirectories(packages, "Microsoft.WindowsTerminal*"))
            {
                string f = Path.Combine(dir, "LocalState", "state.json");
                if (File.Exists(f)) yield return f;
            }
        }
        string unpackaged = Path.Combine(local, "Microsoft", "Windows Terminal", "state.json");
        if (File.Exists(unpackaged)) yield return unpackaged;
    }

    static void Scrub(string file)
    {
        var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        if (root?["persistedWindowLayouts"] is not JsonArray windows || windows.Count == 0) return;

        bool changed = false;
        var keptWindows = new JsonArray();
        foreach (var w in windows.ToList())
        {
            if (w?["tabLayout"] is not JsonArray layout) { keptWindows.Add(w?.DeepClone()); continue; }

            // Actions after a newTab (splitPane, moveFocus…) belong to that tab, so tabs are kept or
            // dropped together with their trailing actions. switchToTab indexes would be stale.
            var keptActions = new JsonArray();
            bool keepingCurrent = true;
            int kept = 0;
            foreach (var action in layout)
            {
                string kind = (string)action?["action"];
                if (kind == "newTab")
                {
                    keepingCurrent = !IsLauncherTab((string)action["commandline"]);
                    if (keepingCurrent) kept++;
                    else changed = true;
                }
                else if (kind == "switchToTab") { changed = true; continue; }
                if (keepingCurrent) keptActions.Add(action.DeepClone());
            }

            if (kept == 0) { changed = true; continue; }
            var copy = w.DeepClone().AsObject();
            copy["tabLayout"] = keptActions;
            keptWindows.Add(copy);
        }
        if (!changed) return;

        File.Copy(file, file + ".csm-bak", overwrite: true);
        root["persistedWindowLayouts"] = keptWindows;
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, true);
    }

    // Matches the command lines Launcher builds.
    public static bool IsLauncherTab(string commandline)
    {
        if (string.IsNullOrEmpty(commandline)) return false;
        var m = EncodedPs.Match(commandline);
        if (m.Success)
        {
            try { return Encoding.Unicode.GetString(Convert.FromBase64String(m.Groups[1].Value)).StartsWith("$Host.UI.RawUI.WindowTitle = "); }
            catch { return false; }
        }
        string c = commandline.TrimStart();
        return c.StartsWith("cmd.exe /k title ", StringComparison.OrdinalIgnoreCase)
            || (c.StartsWith("wsl.exe ", StringComparison.OrdinalIgnoreCase) && c.Contains(" --cd ", StringComparison.Ordinal));
    }
}
