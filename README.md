# Session Startup Manager

[![build](https://github.com/C-Squared-Solutions-LLC/SessionStartupManager/actions/workflows/build.yml/badge.svg)](https://github.com/C-Squared-Solutions-LLC/SessionStartupManager/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

One taskbar click opens a whole set of Claude Code, Codex, Google Antigravity (AGY) sessions and terminal windows, on the right virtual desktop and monitor, each titled correctly.

Windows 10/11 only. Unofficial; not affiliated with Anthropic, OpenAI or Google. Install and sign in to each CLI you want to launch (`claude`, `codex`, `agy`) first; it must be available in your selected PowerShell host.

## Download

Grab a zip from [Releases](https://github.com/C-Squared-Solutions-LLC/SessionStartupManager/releases):

- `…-self-contained.zip`: runs as is, no .NET install needed (bigger file).
- `…-framework-dependent.zip`: small, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

Unzip it somewhere permanent before pinning, because shortcuts and the startup entry point at the exe's location.

Every push to `main` also builds both flavors as workflow artifacts.

## Build from source

```powershell
.\build.ps1        # -> dist\SessionStartupManager.exe  (needs the .NET 8 SDK; close the app first)
```

A release is cut by pushing a tag: `git tag v1.2.3 && git push origin v1.2.3`.

Run the regression checks with `dotnet run --project tests/SessionStartupManager.Tests.csproj -c Release`. They use temporary fixtures and never launch a CLI or change your saved groups.

## Groups

Windows live in groups. Each group has its own settings:

| Setting | What it does |
|---|---|
| Desktop | Switches to that virtual desktop (Ctrl+Win+Arrow) before opening the group's windows. Pick a number past your last desktop and it gets created. |
| Monitor | Moves the group's windows onto that monitor once they're open. |
| Arrange | Tile (grid), Maximize, Cascade, or Keep size. Only applies when a monitor is set. |
| Tabs in one window | One Windows Terminal window with a tab per entry instead of separate windows. |
| Run at Windows startup | Launches the group at sign-in, after the startup delay. |
| Tick in the group list | Includes the group in **Launch all**. |

Groups launch in list order. With **Return to starting desktop** on, you end up back where you started.

**Move to group ▾** moves selected windows between groups.

## Window types

- **Claude Code**: resume a named session, continue the latest one in a folder, or start a new one.
- **Codex**: resume by session ID or name (`codex resume`), continue the latest session in a folder (`codex resume --last`), or start fresh (`codex`). Leave the ID blank in Resume mode to open Codex's picker. See the [Codex CLI reference](https://developers.openai.com/codex/cli/reference#codex-resume).
- **AGY (Antigravity)**: resume by conversation ID (`agy --conversation`), continue the latest conversation in a folder (`agy --continue`), or start fresh (`agy`). Resume mode requires a conversation ID. See [Antigravity's resume documentation](https://antigravity.google/docs/cli/commands/resume).
- **PowerShell 7 / Windows PowerShell / Command Prompt**: a folder plus an optional startup command.
- **WSL**: a distro (Docker Desktop's internal distros are hidden), a Linux or Windows folder, and an optional bash command. The command runs in an interactive bash and leaves you at a prompt. The distro is always passed explicitly because the machine default can be `docker-desktop`.

**Add sessions…** combines all three providers with a provider filter, name/folder/ID search, and optional **Named sessions only** and **Latest per name** filters. Claude sessions come from `~/.claude/projects`; Codex sessions and saved titles come from `$CODEX_HOME` (default `~/.codex`). AGY lists the latest conversation per workspace from `~/.gemini/antigravity-cli/cache/last_conversations.json`; that cache has no conversation titles or per-session timestamps. To add an older AGY conversation, use **Add window…** and paste its ID.

**Add window…** also sets up new or continuing sessions for any provider. **AI sessions run in** chooses the PowerShell host for all three. Extra CLI arguments are passed to the selected provider. Installed `.cmd` shims are preferred over npm's `.ps1` shims when no native executable is on PATH.

The session picker shows assignments across all groups in **Used in groups**. **Latest per name** keeps assigned sessions visible even when a newer session shares their name. A row labeled **Same name: …** has a different ID from the session assigned to that group; both are dimmed so existing usage is easy to spot.

## Window titles

| Entry | Title |
|---|---|
| Claude, resume | The session's `/rename` name (or Claude's auto title), read fresh at every launch. Claude keeps it updated (`✳ Name`). |
| Claude, continue | The name of the newest conversation in that folder. |
| Claude, new | Your configured name, also passed as `--name`. |
| Codex, resume / continue | The saved session title, when available; otherwise your configured name. |
| Codex, new / AGY | Your configured name. The CLI may update the title while running unless it is locked. |
| Shells, WSL | Your configured name. WSL entries lock the title by default because Ubuntu's prompt rewrites it on every command. |

**Lock window title** passes `--suppressApplicationTitle` to Windows Terminal. It needs **Use Windows Terminal** on.

## Taskbar

**Pin Launch all…** and **Pin group to taskbar…** create shortcuts and highlight them in Explorer. Right-click → Show more options → Pin to taskbar. Windows doesn't let apps pin themselves. Every group shortcut has its own AppUserModelID, so each one pins as a separate button. Shift+click any of them to open the manager instead.

## Command line

```
SessionStartupManager.exe                    open the manager
SessionStartupManager.exe --launch           launch every group ticked for Launch all
SessionStartupManager.exe --group <id|name>   launch one group (repeatable)
SessionStartupManager.exe --startup          launch the startup groups (used by the HKCU Run entry)
```

## Notes

- Config: `%APPDATA%\SessionStartupManager\config.json`. Every UI edit saves immediately. On first use, an existing `%APPDATA%\ClaudeStartupManager\config.json` is imported, including groups, session modes and host settings; the original is kept. Older single-list configs are moved into a group called "Main".
- Startup uses one `HKCU\...\Run` value (`SessionStartupManager`). It exists only while at least one enabled group entry is set to run at startup. Saving replaces the legacy `ClaudeStartupManager` startup value to avoid duplicate launches.
- When upgrading, open `SessionStartupManager.exe` once to migrate settings and startup registration. Recreate taskbar pins with **Pin Launch all…** / **Pin group to taskbar…** to use the new name, executable and blue icon. Existing pins still target the old executable.
- Desktop switching goes through Explorer. If Explorer is hung, the group opens on the current desktop and you get a warning.
- Windows are matched by watching for new Windows Terminal / console windows after each launch. Opening other terminals by hand mid-launch can confuse placement.

## License

[MIT](LICENSE)
