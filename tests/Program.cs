using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionStartupManager;

internal static class RegressionChecks
{
    static int _checks;
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [STAThread]
    static void Main(string[] args)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "SessionStartupManager-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            ConfigMigration();
            Commands(fixture);
            SessionDiscovery(fixture);
            PickerAssignments();
            Dialogs();
            Console.WriteLine($"Passed {_checks} regression checks.");
            if (args.Contains("--scan-local"))
            {
                var sessions = SessionScanner.ScanAll(null);
                foreach (var provider in new[] { EntryKind.Claude, EntryKind.Codex, EntryKind.Agy })
                    Console.WriteLine($"Local discovery: {LaunchEntry.KindText(provider)} = {sessions.Count(s => s.Kind == provider)} sessions");
            }
            int inspect = Array.IndexOf(args, "--inspect-picker");
            if (inspect >= 0 && args.Length > inspect + 2)
            {
                var cfg = AppConfig.Deserialize(File.ReadAllText(args[inspect + 1]));
                string cwd = args[inspect + 2];
                string project = Path.Combine(SessionScanner.ProjectsDir, SessionScanner.FolderKey(cwd));
                var sessions = Directory.Exists(project) ? Directory.GetFiles(project, "*.jsonl").Select(SessionScanner.Read).ToList() : new();
                sessions.AddRange(SessionScanner.ScanCodex(SessionScanner.CodexHome).Where(s => string.Equals(s.Cwd, cwd, StringComparison.OrdinalIgnoreCase)));
                sessions.AddRange(SessionScanner.ScanAgy(SessionScanner.AgyHome).Where(s => string.Equals(s.Cwd, cwd, StringComparison.OrdinalIgnoreCase)));
                foreach (var row in SessionPickerModel.Rows(sessions, cfg.Groups, latestOnly: true))
                    Console.WriteLine($"{row.Session.Kind}: {row.Session.Title} [{row.Session.Id}] | {row.UsageText} | {(row.HasUsage ? "dimmed" : "unassigned")}");
            }
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    static void ConfigMigration()
    {
        var legacy = AppConfig.Deserialize("""
            { "ClaudeHost":"powershell", "TabsInOneWindow":true,
              "Entries":[{"Kind":"Claude","ClaudeMode":"Continue","Name":"Old session","SessionId":"saved-id"}] }
            """);
        Check(legacy.SessionHost == "powershell" && legacy.Groups.Single().TabsInOneWindow, "Legacy global settings were lost.");
        var oldEntry = legacy.Groups.Single().Entries.Single();
        Check(oldEntry.SessionMode == SessionMode.Continue && oldEntry.SessionId == "saved-id" && oldEntry.Name == "Old session", "Legacy entries were lost.");

        var grouped = AppConfig.Deserialize("""
            { "Groups":[{"Id":"stable-pin-id","Name":"Work","RunAtStartup":true,"Desktop":2,
              "Entries":[{"Kind":"Claude","ClaudeMode":"New"},{"Kind":4,"Distro":"Ubuntu"}]}] }
            """);
        var group = grouped.Groups.Single();
        Check(group.Id == "stable-pin-id" && group.RunAtStartup && group.Desktop == 2, "Group identities and startup settings must survive migration.");
        Check(group.Entries[0].SessionMode == SessionMode.New && group.Entries[1].Kind == EntryKind.Wsl, "Legacy modes or numeric kind values changed.");

        var current = AppConfig.Deserialize("""
            {"SessionHost":"pwsh","ClaudeHost":"powershell","Groups":[{"Entries":[
              {"Kind":"Codex","SessionMode":"New","ClaudeMode":"Resume"},
              {"Kind":"Agy","SessionMode":"Resume","SessionId":"conversation"}]}]}
            """);
        Check(current.SessionHost == "pwsh" && current.Groups[0].Entries[0].SessionMode == SessionMode.New, "New settings must take priority over old aliases.");
        string saved = JsonSerializer.Serialize(current, Json);
        Check(!saved.Contains("ClaudeHost") && !saved.Contains("ClaudeMode"), "New configs still write Claude-specific settings.");
        var roundtrip = AppConfig.Deserialize(saved);
        var copied = new LaunchEntry();
        copied.CopyFrom(roundtrip.Groups[0].Entries[1].Clone());
        Check(copied.Kind == EntryKind.Agy && copied.SessionId == "conversation", "Provider data didn't survive save, load and duplication.");
        Check(LaunchEntry.SessionKey(EntryKind.Codex, "same") != LaunchEntry.SessionKey(EntryKind.Claude, "same"), "Providers must not share session keys.");
    }

    static void Commands(string fixture)
    {
        string oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fixture);
            string Command(EntryKind kind, SessionMode mode, string id = "") => Launcher.SessionCommand(new()
            {
                Kind = kind, SessionMode = mode, SessionId = id, Name = "Team's window", Arguments = "--model chosen-model",
            });

            Check(Command(EntryKind.Claude, SessionMode.Resume, "abc") == "claude --resume 'abc' --model chosen-model", "Claude resume changed.");
            Check(Command(EntryKind.Claude, SessionMode.Continue) == "claude --continue --model chosen-model", "Claude continue changed.");
            Check(Command(EntryKind.Claude, SessionMode.New) == "claude --name 'Team''s window' --model chosen-model", "Claude naming changed.");
            Check(Command(EntryKind.Codex, SessionMode.Resume, "team's; $name") == "codex resume 'team''s; $name' --model chosen-model", "Codex IDs must be literal PowerShell arguments.");
            Check(Command(EntryKind.Codex, SessionMode.Resume) == "codex resume --model chosen-model", "Blank Codex IDs must open the picker.");
            Check(Command(EntryKind.Codex, SessionMode.Continue) == "codex resume --last --model chosen-model", "Codex continue command is incorrect.");
            Check(Command(EntryKind.Codex, SessionMode.New) == "codex --model chosen-model", "New Codex sessions must not receive Claude flags.");
            Check(Command(EntryKind.Agy, SessionMode.Resume, "conversation") == "agy --conversation 'conversation' --model chosen-model", "AGY resume command is incorrect.");
            Check(Command(EntryKind.Agy, SessionMode.Continue) == "agy --continue --model chosen-model", "AGY continue command is incorrect.");
            Check(Command(EntryKind.Agy, SessionMode.New) == "agy --model chosen-model", "New AGY sessions must not receive Claude flags.");
            bool rejected = false;
            try { Command(EntryKind.Agy, SessionMode.Resume); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "AGY resume without an ID must be rejected before launching.");

            File.WriteAllText(Path.Combine(fixture, "codex.cmd"), "");
            Check(Command(EntryKind.Codex, SessionMode.Continue).StartsWith("codex.cmd resume --last"), "npm .cmd shim should work without changing PowerShell execution policy.");
            File.WriteAllText(Path.Combine(fixture, "codex.exe"), "");
            Check(Command(EntryKind.Codex, SessionMode.Continue).StartsWith("codex.exe resume --last"), "A native CLI must take precedence over a shim.");

            var build = typeof(Launcher).GetMethod("BuildPlan", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var kind in new[] { EntryKind.Claude, EntryKind.Codex, EntryKind.Agy })
            {
                var entry = new LaunchEntry { Kind = kind, SessionMode = SessionMode.New, Name = "Window", WorkingDirectory = fixture };
                object plan = build.Invoke(null, new object[] { new AppConfig { SessionHost = "powershell" }, entry });
                string args = (string)plan.GetType().GetField("Args").GetValue(plan);
                string script = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
                Check(script.StartsWith("$Host.UI.RawUI.WindowTitle = 'Window'\n") && script.EndsWith(Launcher.SessionCommand(entry)), $"{kind} wasn't integrated into the actual launch plan.");
                Check(TerminalState.IsLauncherTab("powershell.exe " + args), $"{kind} is not recognized in Terminal's restored tabs.");
            }
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    static void SessionDiscovery(string fixture)
    {
        string codexHome = Path.Combine(fixture, "codex");
        string logs = Path.Combine(codexHome, "sessions", "2026", "11", "02");
        Directory.CreateDirectory(logs);
        string first = Path.Combine(logs, "rollout-a.jsonl");
        File.WriteAllText(first, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "first-id", cwd = "C:\\Work\\One" } }) + "\n");
        File.WriteAllText(Path.Combine(logs, "rollout-b.jsonl"), """
            { "type": "session_meta", "payload": { "id": "second-id", "cwd": "C:\\Work\\Two" } }
            {"unfinished":
            """);
        File.WriteAllText(Path.Combine(logs, "broken.jsonl"), "not a session");
        File.WriteAllText(Path.Combine(codexHome, "session_index.jsonl"), """
            {"id":"first-id","thread_name":"Old title"}
            {"incomplete":
            {"id":"first-id","thread_name":"Renamed session"}
            {"id":123,"thread_name":"Invalid record"}
            []
            """);
        File.SetLastWriteTime(first, DateTime.Now.AddDays(-1));
        var codex = SessionScanner.ScanCodex(codexHome);
        Check(codex.Count == 2 && codex[0].Id == "second-id", "Codex discovery must tolerate damaged logs and sort newest first.");
        Check(codex.Single(s => s.Id == "first-id") is { Title: "Renamed session", Named: true, Cwd: "C:\\Work\\One", Kind: EntryKind.Codex }, "Codex title index or escaped folder decoding failed.");
        Check(!codex[0].Named && codex[0].Title == null, "Sessions without a saved title should remain selectable.");
        Check(SessionScanner.ScanCodex(Path.Combine(fixture, "missing")).Count == 0, "Missing Codex installs must be harmless.");

        File.WriteAllText(Path.Combine(logs, "rollout-review.jsonl"), """
            {"type":"session_meta","payload":{"session_id":"first-id","id":"review-id","cwd":"C:\\Work\\One"}}
            """);
        var withReview = SessionScanner.ScanCodex(codexHome);
        Check(withReview.Count == 3 && withReview.Any(s => s.Id == "review-id") && withReview.Any(s => s.Id == "first-id"),
            "A Codex child thread must not reuse its parent's session_id or hide its parent.");
        File.Delete(Path.Combine(logs, "rollout-review.jsonl"));

        string oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Check(SessionScanner.CodexHome == codexHome, "CODEX_HOME override was ignored.");
            Check(SessionScanner.TitleForSession(EntryKind.Codex, "first-id") == "Renamed session", "Codex titles weren't resolved at launch.");
            Check(SessionScanner.TitleForLatestInFolder(EntryKind.Codex, "c:/work/one/") == "Renamed session", "Continue title lookup must match Windows folder spellings.");
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome); }

        string agyHome = Path.Combine(fixture, "agy");
        Directory.CreateDirectory(Path.Combine(agyHome, "cache"));
        string cache = Path.Combine(agyHome, "cache", "last_conversations.json");
        File.WriteAllText(cache, """
            {"C:\\Work\\One":"conversation-id","C:\\Work\\Empty":"", "bad-value":42}
            """);
        var agy = SessionScanner.ScanAgy(agyHome);
        Check(agy.Count == 1 && agy[0] is { Kind: EntryKind.Agy, Id: "conversation-id", Cwd: "C:\\Work\\One", Named: false }, "AGY workspace cache wasn't read correctly.");
        Check(agy[0].LastUsed == DateTime.MinValue, "AGY cache must not invent per-session timestamps.");
        File.WriteAllText(cache, "{partial");
        Check(SessionScanner.ScanAgy(agyHome).Count == 0, "A partially written AGY cache must not break other providers.");
        Check(SessionScanner.ScanAgy(Path.Combine(fixture, "missing-agy")).Count == 0, "Missing AGY installs must be harmless.");

        string claude = Path.Combine(fixture, "legacy-session.jsonl");
        File.WriteAllText(claude, """
            {"cwd":"C:\\Work\\Claude","type":"user"}
            {"customTitle":"Original"}
            {"customTitle":"Latest name"}
            """);
        var original = SessionScanner.Read(claude);
        Check(original.Kind == EntryKind.Claude && original.Title == "Latest name" && original.Cwd == "C:\\Work\\Claude", "Claude discovery regressed.");
    }

    static void Dialogs()
    {
        Application.EnableVisualStyles();
        foreach (var kind in new[] { EntryKind.Claude, EntryKind.Codex, EntryKind.Agy })
        {
            using var entry = new EntryDialog(new LaunchEntry { Kind = kind }, Array.Empty<LaunchGroup>());
            using var picker = new SessionPickerDialog(false, Array.Empty<LaunchGroup>(), kind);
            Check(entry.Controls.Count > 0 && picker.Text.Contains(LaunchEntry.KindText(kind)), $"{kind} dialog failed to initialize.");
        }
    }

    static void PickerAssignments()
    {
        var now = DateTime.Now;
        var older = new SessionInfo { Kind = EntryKind.Claude, Id = "claude-used", Title = "LastHold", Cwd = "C:\\Work\\LastHold", Named = true, LastUsed = now.AddDays(-1) };
        var newer = new SessionInfo { Kind = EntryKind.Claude, Id = "claude-new", Title = "LastHold", Cwd = "c:/work/lasthold/", Named = true, LastUsed = now };
        var unusedOlder = new SessionInfo { Kind = EntryKind.Claude, Id = "claude-unused", Title = "LastHold", Cwd = older.Cwd, Named = true, LastUsed = now.AddDays(-3) };
        var codex = new SessionInfo { Kind = EntryKind.Codex, Id = "codex-used", Title = "Last Hold", Cwd = older.Cwd, Named = true, LastUsed = now };
        var otherProvider = new SessionInfo { Kind = EntryKind.Agy, Id = older.Id, Title = older.Title, Cwd = older.Cwd, LastUsed = now };
        var otherFolder = new SessionInfo { Kind = EntryKind.Claude, Id = "elsewhere", Title = older.Title, Cwd = "C:\\Work\\Other", Named = true, LastUsed = now };
        var sessions = new[] { older, newer, unusedOlder, codex, otherProvider, otherFolder };
        var usedEntry = new LaunchEntry { Kind = EntryKind.Claude, SessionMode = SessionMode.Resume, SessionId = " CLAUDE-USED ", WorkingDirectory = older.Cwd };
        var middle = new LaunchGroup { Name = "Development Middle", Entries = new()
        {
            usedEntry,
            new() { Kind = EntryKind.Codex, SessionMode = SessionMode.Resume, SessionId = codex.Id, WorkingDirectory = codex.Cwd },
        } };
        var right = new LaunchGroup { Name = "Development Right" };
        var groups = new[] { middle, right };
        var rows = SessionPickerModel.Rows(sessions, groups, latestOnly: true);
        var oldRow = rows.Single(r => r.Session.Id == older.Id && r.Session.Kind == EntryKind.Claude);
        Check(oldRow.Groups.SequenceEqual(new[] { middle.Name }), "Latest per name hid the configured older session's group.");
        Check(rows.Single(r => r.Session.Id == codex.Id).Groups.SequenceEqual(new[] { middle.Name }), "The other provider's configured session must also show its group.");
        var newRow = rows.Single(r => r.Session.Id == newer.Id);
        Check(newRow.Groups.Length == 0 && newRow.SameNameGroups.SequenceEqual(new[] { middle.Name }) && newRow.HasUsage,
            "An unassigned namesake must be distinguished from the assigned ID while showing the related group.");
        Check(newRow.UsageText == "Same name: Development Middle", "Same-name usage must be explicit, not reported as an exact assignment.");
        Check(!rows.Any(r => r.Session.Id == unusedOlder.Id), "Latest per name must still collapse older unassigned namesakes.");
        Check(!rows.Single(r => r.Session == otherProvider).HasUsage && !rows.Single(r => r.Session == otherFolder).HasUsage,
            "Usage must not leak across providers or folders with the same title.");
        Check(SessionPickerModel.Rows(sessions, groups, latestOnly: false).Count == sessions.Length, "Disabling the latest filter must show each session.");
        var autoTitle = new SessionInfo { Kind = older.Kind, Id = "auto-title", Title = older.Title, Cwd = older.Cwd, Named = false, LastUsed = now.AddDays(1) };
        var namedRows = SessionPickerModel.Rows(new[] { autoTitle, older }, Array.Empty<LaunchGroup>(), latestOnly: true, namedOnly: true);
        Check(namedRows.Count == 1 && namedRows[0].Session == older, "An automatic title must not hide the latest explicitly named session under Named sessions only.");

        right.Entries.Add(usedEntry.Clone());
        var shared = SessionPickerModel.Rows(sessions, groups, true).Single(r => r.Session == older);
        Check(shared.Groups.SequenceEqual(new[] { middle.Name, right.Name }), "Every group using the same session must be shown.");
        middle.Entries.Remove(usedEntry);
        var moved = SessionPickerModel.Rows(sessions, groups, true).Single(r => r.Session == older);
        Check(moved.Groups.SequenceEqual(new[] { right.Name }), "Assignment changes must refresh even when scanned sessions are cached.");

        right.Entries[0].SessionMode = SessionMode.New;
        Check(!SessionPickerModel.Rows(sessions, groups, true).Single(r => r.Session == newer).HasUsage,
            "New-session entries must not mark a stale saved ID as used.");
        right.Entries[0].SessionMode = SessionMode.Continue;
        var continuing = SessionPickerModel.Rows(sessions, groups, true);
        Check(continuing.Single(r => r.Session == newer).Groups.SequenceEqual(new[] { right.Name }),
            "Continue mode must mark the newest session in its folder.");
        right.Entries[0].SessionMode = SessionMode.Resume;
        right.Entries[0].SessionId = "LastHold";
        Check(SessionPickerModel.Rows(sessions, groups, true).Single(r => r.Session == newer).Groups.SequenceEqual(new[] { right.Name }),
            "A resume entry configured by session name must resolve to the matching folder and provider.");

        // Exercise the dialog's actual row styling and group column, not only the model.
        middle.Entries.Add(usedEntry);
        right.Entries.Clear();
        var cacheField = typeof(SessionPickerDialog).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static);
        object oldCache = cacheField.GetValue(null);
        cacheField.SetValue(null, sessions.ToList());
        try
        {
            using var picker = new SessionPickerDialog(true, groups);
            typeof(SessionPickerDialog).GetMethod("Fill", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(picker, null);
            var list = (ListView)typeof(SessionPickerDialog).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(picker);
            var usedItem = list.Items.Cast<ListViewItem>().Single(i => ReferenceEquals(i.Tag, older));
            var relatedItem = list.Items.Cast<ListViewItem>().Single(i => ReferenceEquals(i.Tag, newer));
            Check(usedItem.ForeColor == Theme.Muted && usedItem.SubItems[2].Text == middle.Name, "The picker must dim assigned sessions and show their owning group.");
            Check(relatedItem.ForeColor == Theme.Muted && relatedItem.SubItems[2].Text == "Same name: " + middle.Name,
                "The picker must label and dim a namesake instead of leaving it white and unexplained.");
        }
        finally { cacheField.SetValue(null, oldCache); }
    }
}
