# Simple Game Launcher

A small, native Windows 11 launcher for a folder of game shortcuts. Point it at a folder full of `.lnk` / `.url` shortcuts and it turns them into a searchable grid of cover-art tiles — no browser, no account, no library import.

## Features

- **Live folder watching** — add, remove or rename a shortcut and the grid updates within a second.
- **Automatic cover art** from Steam (by app id for Steam links, by exact name for everything else), cached locally. Custom covers per game.
- **Trainers** — put trainer `.exe`s in a `z` subfolder; they're matched to games by name and get a TRAINER badge. Ctrl+Enter starts the game, then the trainer after a delay.
- **Launcher bar** from a `Launchers` subfolder (Steam, Epic, EA, Battle.net, …).
- **Filters** — Games (default), All, Recent, With trainer, one tab per subfolder (e.g. `Tools`), one per tag.
- **Arrange mode** — drag and drop tiles into a custom order, Alt+arrows to nudge, reset to A–Z.
- **Per-filter wallpapers** with dim and blur, adjustable card size, Mica window, global hotkey (default `Win+Shift+G`), tray icon, start with Windows.

## Folder layout it understands

```
My Games Folder\
├─ Some Game.lnk           ← games (any .lnk / .url)
├─ Another Game.url
├─ Launchers\              ← shown as the launcher bar
├─ z\                      ← trainers (*Trainer*.exe / .lnk), matched to games by name
└─ Tools\                  ← any other subfolder becomes its own filter tab
                             (Tools is excluded from the Games filter)
```

## Requirements

- Windows 11 (Windows 10 works without the Mica background)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run, .NET 10 SDK to build

## Build and run

```powershell
dotnet run                                   # debug run
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o app
```

The published `app\GameLauncher.exe` is a single file. On first start it opens Settings so you can pick your games folder.

## Keyboard

| Keys | Action |
| --- | --- |
| type anything | search (name, initials like `bmw`, tags, folder) |
| Arrows · Enter | select · play |
| Ctrl+Enter | play with trainer |
| Esc | clear search / hide |
| Ctrl+Tab | next filter |
| Ctrl + / − / 0, Ctrl+wheel | card size |
| Ctrl+, | settings |
| F5 | rescan |

## Data

Everything the launcher remembers lives next to the exe: `launcher-data.json` (settings, tags, trainer choices, play history, custom order), `covers\` and `wallpapers\`. Shortcuts themselves are never modified.
