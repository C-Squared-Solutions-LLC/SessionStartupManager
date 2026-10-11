using Microsoft.Win32;

namespace ClaudeStartupManager;

// One HKCU Run value covers every startup group. A single process launches them in order,
// so groups on different desktops don't fight over desktop switching.
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeStartupManager";

    public static void Sync(AppConfig cfg)
    {
        bool wanted = cfg.Groups.Any(g => g.RunAtStartup && g.Entries.Any(e => e.Enabled));
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (wanted) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
