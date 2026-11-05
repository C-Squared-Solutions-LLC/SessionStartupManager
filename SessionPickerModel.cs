namespace SessionStartupManager;

internal sealed record SessionPickerRow(SessionInfo Session, string[] Groups, string[] SameNameGroups)
{
    public bool HasUsage => Groups.Length > 0 || SameNameGroups.Length > 0;
    public string UsageText => string.Join("; ", new[]
    {
        string.Join(", ", Groups),
        SameNameGroups.Length == 0 ? "" : "Same name: " + string.Join(", ", SameNameGroups),
    }.Where(s => s.Length > 0));
}

internal static class SessionPickerModel
{
    // Resolve assignments before collapsing names. A newer session with the same title
    // must not hide the actual session already configured in another group.
    public static List<SessionPickerRow> Rows(IEnumerable<SessionInfo> sessions, IEnumerable<LaunchGroup> groups, bool latestOnly, bool namedOnly = false)
    {
        var ordered = sessions.Where(s => !string.IsNullOrWhiteSpace(s.Cwd))
            .OrderByDescending(s => s.LastUsed).ToList();
        var byId = ordered.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var assignments = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups ?? Enumerable.Empty<LaunchGroup>())
        {
            foreach (var entry in group.Entries.Where(e => LaunchEntry.IsSession(e.Kind)))
            {
                SessionInfo session = null;
                if (entry.SessionMode == SessionMode.Resume && !string.IsNullOrWhiteSpace(entry.SessionId))
                {
                    byId.TryGetValue(LaunchEntry.SessionKey(entry.Kind, entry.SessionId), out session);
                    // Claude and Codex also accept a session name instead of a UUID.
                    if (session == null && entry.Kind != EntryKind.Agy && !Guid.TryParse(entry.SessionId, out _))
                        session = ordered.FirstOrDefault(s => s.Kind == entry.Kind
                            && string.Equals(s.Title, entry.SessionId.Trim(), StringComparison.OrdinalIgnoreCase)
                            && Folder(s.Cwd) == Folder(entry.WorkingDirectory));
                }
                else if (entry.SessionMode == SessionMode.Continue)
                    session = ordered.FirstOrDefault(s => s.Kind == entry.Kind && Folder(s.Cwd) == Folder(entry.WorkingDirectory));

                if (session == null) continue;
                if (!assignments.TryGetValue(session.Key, out var names)) assignments[session.Key] = names = new(StringComparer.OrdinalIgnoreCase);
                names.Add(group.Name);
            }
        }

        var rows = new List<SessionPickerRow>();
        foreach (var family in ordered.GroupBy(s => (s.Kind, Name(s), Folder(s.Cwd))))
        {
            var familyGroups = family.SelectMany(s => assignments.GetValueOrDefault(s.Key) ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool first = true;
            foreach (var session in family.Where(s => !namedOnly || s.Named))
            {
                string[] assigned = assignments.TryGetValue(session.Key, out var names) ? names.ToArray() : Array.Empty<string>();
                // Keep every assigned ID visible, even when an unassigned namesake is newer.
                if (!latestOnly || first || assigned.Length > 0)
                    rows.Add(new(session, assigned, familyGroups.Except(assigned, StringComparer.OrdinalIgnoreCase).ToArray()));
                first = false;
            }
        }
        return rows.OrderByDescending(r => r.Session.LastUsed).ToList();
    }

    static string Name(SessionInfo session) => string.IsNullOrWhiteSpace(session.Title)
        ? "id:" + session.Id.ToUpperInvariant() : "name:" + session.Title.Trim().ToUpperInvariant();

    static string Folder(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path?.Trim().Trim('"') ?? "");
        string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 0) path = home;
        else if (path == "~" || path.StartsWith("~\\") || path.StartsWith("~/")) path = home + path[1..];
        return path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }
}
