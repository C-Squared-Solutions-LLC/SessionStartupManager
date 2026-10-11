using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeStartupManager;

internal sealed class SessionInfo
{
    public string Id { get; init; }
    public string FilePath { get; init; }
    public string Cwd { get; init; }
    public string Title { get; init; }
    public bool Named { get; init; }
    public DateTime LastUsed { get; init; }
}

// Reads Claude Code's session logs (~/.claude/projects/<folder-key>/<session-id>.jsonl).
// Logs get big, so only the head (cwd) and a bounded tail (latest title records) are read.
internal static class SessionScanner
{
    const int HeadBytes = 64 * 1024;
    const int Chunk = 256 * 1024;
    const int TailBudget = 2 * 1024 * 1024;

    static readonly Regex CustomTitleRx = new("\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.RightToLeft | RegexOptions.Compiled);
    static readonly Regex AiTitleRx = new("\"aiTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.RightToLeft | RegexOptions.Compiled);
    static readonly Regex CwdRx = new("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    public static string ProjectsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    // Same mangling Claude Code uses: C:\Users\me\Proj -> C--Users-me-Proj
    public static string FolderKey(string cwd) => Regex.Replace(cwd.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    public static List<SessionInfo> ScanAll(IProgress<string> progress)
    {
        if (!Directory.Exists(ProjectsDir)) return new();
        var files = Directory.EnumerateDirectories(ProjectsDir)
            .SelectMany(d => { try { return Directory.GetFiles(d, "*.jsonl"); } catch { return Array.Empty<string>(); } })
            .ToList();

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

    public static string TitleForSession(string sessionId)
    {
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

    public static string TitleForLatestInFolder(string cwd)
    {
        try
        {
            string dir = Path.Combine(ProjectsDir, FolderKey(cwd));
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest == null ? null : Read(newest.FullName).Title;
        }
        catch { return null; }
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
