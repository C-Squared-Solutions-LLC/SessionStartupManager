using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStartupManager;

internal enum EntryKind { Claude, Pwsh, WindowsPowerShell, Cmd, Wsl }

internal enum ClaudeMode { Resume, Continue, New }

internal enum ArrangeMode { Tile, Maximize, Cascade, KeepSize }

internal sealed class LaunchEntry
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public EntryKind Kind { get; set; } = EntryKind.Claude;
    public string WorkingDirectory { get; set; } = "";
    public ClaudeMode ClaudeMode { get; set; } = ClaudeMode.Resume;
    public string SessionId { get; set; } = "";

    // WSL only. Blank means wsl.exe's default distro.
    public string Distro { get; set; } = "";

    // Claude: extra CLI args. Shells: command to run once the window is up.
    public string Arguments { get; set; } = "";

    // Pins the window title so the running program can't change it (Windows Terminal only).
    public bool LockTitle { get; set; }

    public LaunchEntry Clone() => (LaunchEntry)MemberwiseClone();

    public void CopyFrom(LaunchEntry o)
    {
        Enabled = o.Enabled;
        Name = o.Name;
        Kind = o.Kind;
        WorkingDirectory = o.WorkingDirectory;
        ClaudeMode = o.ClaudeMode;
        SessionId = o.SessionId;
        Distro = o.Distro;
        Arguments = o.Arguments;
        LockTitle = o.LockTitle;
    }

    public static string KindText(EntryKind k) => k switch
    {
        EntryKind.Claude => "Claude Code",
        EntryKind.Pwsh => "PowerShell 7",
        EntryKind.WindowsPowerShell => "Windows PowerShell",
        EntryKind.Cmd => "Command Prompt",
        EntryKind.Wsl => "WSL",
        _ => k.ToString(),
    };

    public static string ModeText(ClaudeMode m) => m switch
    {
        ClaudeMode.Resume => "Resume session",
        ClaudeMode.Continue => "Continue latest in folder",
        ClaudeMode.New => "New session",
        _ => m.ToString(),
    };
}

internal sealed class LaunchGroup
{
    // Stable id so pinned shortcuts (--group <id>) survive renames.
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "New group";
    public bool IncludeInLaunchAll { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool TabsInOneWindow { get; set; }

    // 0 = whichever desktop is active; otherwise 1-based position in Task View.
    public int Desktop { get; set; }

    // Screen.DeviceName ("\\.\DISPLAY2"); empty = leave windows where Windows opens them.
    public string Monitor { get; set; } = "";

    // Bounds at the time the monitor was picked, used if the device name changes.
    public string MonitorBounds { get; set; } = "";

    public ArrangeMode Arrange { get; set; } = ArrangeMode.Tile;
    public List<LaunchEntry> Entries { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public static string ArrangeText(ArrangeMode m) => m switch
    {
        ArrangeMode.Tile => "Tile",
        ArrangeMode.Maximize => "Maximize",
        ArrangeMode.Cascade => "Cascade",
        ArrangeMode.KeepSize => "Keep size",
        _ => m.ToString(),
    };
}

internal sealed class AppConfig
{
    public bool UseWindowsTerminal { get; set; } = true;
    public string ClaudeHost { get; set; } = "pwsh";
    public int DelayMs { get; set; } = 200;
    public int StartupDelaySec { get; set; } = 20;
    public bool ReturnToStartingDesktop { get; set; } = true;
    public List<LaunchGroup> Groups { get; set; } = new();

    // Pre-groups config shape; folded into a group on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LaunchEntry> Entries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TabsInOneWindow { get; set; }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeStartupManager");

    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LaunchGroup FindGroup(string key) =>
        Groups.FirstOrDefault(g => string.Equals(g.Id, key, StringComparison.OrdinalIgnoreCase))
        ?? Groups.FirstOrDefault(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase));

    void Normalize()
    {
        Groups ??= new();
        if (Groups.Count == 0)
        {
            Groups.Add(new LaunchGroup
            {
                Name = "Main",
                Entries = Entries ?? new(),
                TabsInOneWindow = TabsInOneWindow ?? false,
            });
        }
        Entries = null;
        TabsInOneWindow = null;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups)
        {
            g.Entries ??= new();
            g.Monitor ??= "";
            g.MonitorBounds ??= "";
            if (string.IsNullOrWhiteSpace(g.Id) || !ids.Add(g.Id))
            {
                g.Id = LaunchGroup.NewId();
                ids.Add(g.Id);
            }
        }
    }

    public static AppConfig Load(out string error)
    {
        error = null;
        AppConfig cfg = null;
        if (File.Exists(FilePath))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json);
            }
            catch (Exception ex)
            {
                // Keep the broken file instead of silently overwriting it on the next save.
                string bad = FilePath + ".bad";
                try { File.Copy(FilePath, bad, true); } catch { }
                error = $"Couldn't read {FilePath}: {ex.Message}\nA copy was saved to {bad}.";
            }
        }
        cfg ??= new AppConfig();
        cfg.Normalize();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }
}
