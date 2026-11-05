using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionStartupManager;

internal sealed class SessionInfo
{
    public EntryKind Kind { get; init; } = EntryKind.Claude;
    public string Key => LaunchEntry.SessionKey(Kind, Id);
    public string Id { get; init; }
    public string FilePath { get; init; }
    public string Cwd { get; init; }
    public string Title { get; init; }
    public bool Named { get; init; }
    public DateTime LastUsed { get; init; }
}

// Reads local session metadata without launching or modifying any provider's sessions.
internal static class SessionScanner
{
    const int HeadBytes = 64 * 1024;
    const int Chunk = 256 * 1024;
    const int TailBudget = 2 * 1024 * 1024;

    static readonly Regex CustomTitleRx = new("\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.RightToLeft | RegexOptions.Compiled);
    static readonly Regex AiTitleRx = new("\"aiTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.RightToLeft | RegexOptions.Compiled);
    static readonly Regex CwdRx = new("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    // Known-folder lookup can be unavailable under a restricted Windows token.
    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is string home && home.Length > 0
        ? home : Environment.GetEnvironmentVariable("USERPROFILE") ?? "";

    public static string ProjectsDir => Path.Combine(UserProfile, ".claude", "projects");

    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is string home && !string.IsNullOrWhiteSpace(home)
        ? Environment.ExpandEnvironmentVariables(home)
        : Path.Combine(UserProfile, ".codex");

    public static string AgyHome => Path.Combine(UserProfile, ".gemini", "antigravity-cli");

    // Same mangling Claude Code uses: C:\Users\me\Proj -> C--Users-me-Proj
    public static string FolderKey(string cwd) => Regex.Replace(cwd.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    public static List<SessionInfo> ScanAll(IProgress<string> progress)
    {
        var sessions = ScanClaude(progress);
        progress?.Report("Scanning Codex sessions…");
        sessions.AddRange(ScanCodex(CodexHome));
        progress?.Report("Scanning AGY's recent workspaces…");
        sessions.AddRange(ScanAgy(AgyHome));
        return sessions.OrderByDescending(s => s.LastUsed).ToList();
    }

    static List<SessionInfo> ScanClaude(IProgress<string> progress)
    {
        if (!Directory.Exists(ProjectsDir)) return new();
        List<string> files;
        try
        {
            files = Directory.EnumerateDirectories(ProjectsDir)
                .SelectMany(d => { try { return Directory.GetFiles(d, "*.jsonl"); } catch { return Array.Empty<string>(); } })
                .ToList();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }

        var found = new ConcurrentBag<SessionInfo>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
        {
            try { found.Add(Read(f)); } catch { }
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0) progress?.Report($"Scanning sessions {n}/{files.Count}…");
        });
        return found.OrderByDescending(s => s.LastUsed).ToList();
    }

    public static string TitleForSession(EntryKind kind, string sessionId)
    {
        if (kind == EntryKind.Codex) return CodexTitles(CodexHome).GetValueOrDefault(sessionId);
        if (kind != EntryKind.Claude) return null; // AGY's cache has IDs and folders, not conversation titles.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            {
                string p = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(p)) return Read(p).Title;
            }
        }
        catch { }
        return null;
    }

    public static string TitleForLatestInFolder(EntryKind kind, string cwd)
    {
        if (kind == EntryKind.Codex)
            return ScanCodex(CodexHome).FirstOrDefault(s => SameFolder(s.Cwd, cwd))?.Title;
        if (kind != EntryKind.Claude) return null;
        try
        {
            string dir = Path.Combine(ProjectsDir, FolderKey(cwd));
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest == null ? null : Read(newest.FullName).Title;
        }
        catch { return null; }
    }

    static bool SameFolder(string a, string b) => string.Equals(
        a?.Replace('/', '\\').TrimEnd('\\'), b?.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static readonly Regex CodexIdRx = new("\"id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex CodexLegacyIdRx = new("\"session_id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex CodexCwdRx = new("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    internal static List<SessionInfo> ScanCodex(string home)
    {
        var sessions = new List<SessionInfo>();
        string dir = Path.Combine(home, "sessions");
        if (!Directory.Exists(dir)) return sessions;
        var titles = CodexTitles(home);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string path in Directory.EnumerateFiles(dir, "*.jsonl", options))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    string head = ReadText(fs, 0, (int)Math.Min(fs.Length, HeadBytes));
                    // Subagents can inherit session_id from their parent; id identifies this thread.
                    string id = Value(CodexIdRx.Match(head)) ?? Value(CodexLegacyIdRx.Match(head));
                    string cwd = Value(CodexCwdRx.Match(head));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd)) continue;
                    string title = titles.GetValueOrDefault(id);
                    sessions.Add(new SessionInfo
                    {
                        Kind = EntryKind.Codex, Id = id, FilePath = path, Cwd = cwd,
                        Title = title, Named = title != null, LastUsed = File.GetLastWriteTime(path),
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return sessions.OrderByDescending(s => s.LastUsed).DistinctBy(s => s.Key).ToList();
    }

    static Dictionary<string, string> CodexTitles(string home)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = new FileStream(Path.Combine(home, "session_index.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is string line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && root.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()) && !string.IsNullOrWhiteSpace(name.GetString()))
                        titles[id.GetString()] = name.GetString().Trim(); // index is append-only; last rename wins
                }
                catch (JsonException) { } // a concurrently appended final record may be incomplete
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return titles;
    }

    // AGY documents this cache as workspace -> most recent conversation ID. Older IDs
    // can still be entered manually; do not treat its private conversation databases as JSON.
    internal static List<SessionInfo> ScanAgy(string home)
    {
        var sessions = new List<SessionInfo>();
        string path = Path.Combine(home, "cache", "last_conversations.json");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return sessions;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.String) continue;
                string id = entry.Value.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                string folder = entry.Name.TrimEnd('\\', '/');
                sessions.Add(new SessionInfo
                {
                    Kind = EntryKind.Agy, Id = id.Trim(), FilePath = path, Cwd = entry.Name,
                    Title = "AGY · " + folder.Split('\\', '/').Last(), Named = false,
                    // The cache does not contain per-conversation timestamps.
                    LastUsed = DateTime.MinValue,
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return sessions;
    }

    public static SessionInfo Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;

        string cwd = Value(CwdRx.Match(ReadText(fs, 0, (int)Math.Min(len, HeadBytes))));
        string custom = null, ai = null;

        // Walk backwards from the end; the last title record wins.
        long end = len, budget = TailBudget;
        while (end > 0 && budget > 0 && custom == null)
        {
            long start = Math.Max(0, end - Chunk);
            string text = ReadText(fs, start, (int)(end - start));
            custom = Value(CustomTitleRx.Match(text));
            ai ??= Value(AiTitleRx.Match(text));
            cwd ??= Value(CwdRx.Match(text));
            budget -= end - start;
            if (start == 0) break;
            end = start + 1024; // overlap so a record split across chunks still matches
        }

        return new SessionInfo
        {
            Id = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            Cwd = cwd,
            Title = custom ?? ai,
            Named = custom != null,
            LastUsed = File.GetLastWriteTime(path),
        };
    }

    static string ReadText(FileStream fs, long start, int count)
    {
        var buf = new byte[count];
        fs.Seek(start, SeekOrigin.Begin);
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buf, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    static string Value(Match m)
    {
        if (!m.Success) return null;
        string raw = m.Groups[1].Value;
        string s;
        try { s = JsonSerializer.Deserialize<string>("\"" + raw + "\""); }
        catch { s = raw; }
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
