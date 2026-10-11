# Claude Startup Manager

[![build](https://github.com/Neelerak/ClaudeStartupManager/actions/workflows/build.yml/badge.svg)](https://github.com/Neelerak/ClaudeStartupManager/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

One taskbar click opens a whole set of [Claude Code](https://claude.com/claude-code) sessions and terminal windows, on the right virtual desktop and monitor, each titled correctly.

Windows 10/11 only. Unofficial; not affiliated with Anthropic.

## Download

Grab a zip from [Releases](https://github.com/Neelerak/ClaudeStartupManager/releases):

- `…-self-contained.zip`: runs as is, no .NET install needed (bigger file).
- `…-framework-dependent.zip`: small, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

Unzip it somewhere permanent before pinning, because shortcuts and the startup entry point at the exe's location.

Every push to `main` also builds both flavors as workflow artifacts.

## Build from source

```powershell
.\build.ps1        # -> dist\ClaudeStartupManager.exe  (needs the .NET 8 SDK; close the app first)
```

A release is cut by pushing a tag: `git tag v1.2.3 && git push origin v1.2.3`.

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

- **Claude Code**: resume a named session, continue the latest one in a folder, or start a new one. **Add Claude sessions…** lists every session you've named with `/rename`.
- **PowerShell 7 / Windows PowerShell / Command Prompt**: a folder plus an optional startup command.
- **WSL**: a distro (Docker Desktop's internal distros are hidden), a Linux or Windows folder, and an optional bash command. The command runs in an interactive bash and leaves you at a prompt. The distro is always passed explicitly because the machine default can be `docker-desktop`.

## Window titles

| Entry | Title |
|---|---|
| Claude, resume | The session's `/rename` name (or Claude's auto title), read fresh at every launch. Claude keeps it updated (`✳ Name`). |
| Claude, continue | The name of the newest conversation in that folder. |
| Claude, new | Your configured name, also passed as `--name`. |
| Shells, WSL | Your configured name. WSL entries lock the title by default because Ubuntu's prompt rewrites it on every command. |

**Lock window title** passes `--suppressApplicationTitle` to Windows Terminal. It needs **Use Windows Terminal** on.

## Taskbar

**Pin Launch all…** and **Pin group to taskbar…** create shortcuts and highlight them in Explorer. Right-click → Show more options → Pin to taskbar. Windows doesn't let apps pin themselves. Every group shortcut has its own AppUserModelID, so each one pins as a separate button. Shift+click any of them to open the manager instead.

## Command line

```
ClaudeStartupManager.exe                    open the manager
ClaudeStartupManager.exe --launch           launch every group ticked for Launch all
ClaudeStartupManager.exe --group <id|name>  launch one group (repeatable)
ClaudeStartupManager.exe --startup          launch the startup groups (used by the HKCU Run entry)
```

## Notes

- Config: `%APPDATA%\ClaudeStartupManager\config.json`. Every UI edit saves immediately. Older single-list configs are moved into a group called "Main".
- Startup uses one `HKCU\...\Run` value (`ClaudeStartupManager`). It exists only while at least one group is set to run at startup.
- Desktop switching goes through Explorer. If Explorer is hung, the group opens on the current desktop and you get a warning.
- Windows are matched by watching for new Windows Terminal / console windows after each launch. Opening other terminals by hand mid-launch can confuse placement.

## License

[MIT](LICENSE)
