# Simple Game Launcher

A small, native Windows 11 launcher for a folder of game shortcuts. Point it at a folder full of `.lnk` / `.url` shortcuts and it turns them into a searchable grid of cover-art tiles: no browser, no account, no library import.

![Simple Game Launcher: a grid of game covers with a launcher bar and filter tabs](docs/screenshots/main.jpg)

## Download

1. Get **GameLauncher.exe** from the [latest release](https://github.com/iosifv/simple-game-launcher/releases/latest).
2. Put it in a folder of its own, for example `Documents\Game Launcher`. It keeps its settings and cover art next to itself, so avoid `Program Files`.
3. Double-click it. On first start it asks for your games folder: the folder with your game shortcuts.

It's a single file with everything included: no installer, nothing else to install. Windows may show a SmartScreen warning because the exe isn't signed: click **More info → Run anyway**.

## Features

**Your games, at a glance**
- **Cover art found for you.** Steam art for Steam games; for everything else the Microsoft Store (Xbox) and GOG are searched by name. Covers are cached locally.
- **Find cover online…** (right-click a game → Cover): search Steam, the Microsoft Store and GOG yourself, preview the results and pick a landscape and a portrait image separately. Or use any image file.
- **Landscape or portrait cards**, switched with one click, with or without names under them.
- **Tiles without a cover** get a soft glow in the colours of the game's icon.
- **Hover effect:** the card lifts, the art zooms, a light sweeps across it and it glows in the cover's own colours.

![Portrait cards with names, Steam library art and an automatic Microsoft Store cover for Genshin Impact](docs/screenshots/portrait-cards.jpg)

**Find and start games fast**
- **Just start typing** to search by name, initials (`bmw` → Black Myth Wukong), tag or folder. Enter plays.
- **Launcher bar** with your store apps (Steam, Epic, EA, Battle.net, …) from a `Launchers` subfolder.
- **Filters:** Games, All, Recent, With trainer, one tab per subfolder (e.g. `Tools`) and one per tag.
- **Global hotkey** (default `Win+Shift+G`) and a tray icon: the launcher is always one keypress away.
- **Live folder watching:** add, remove or rename a shortcut and the grid updates within a second.

**Make it yours**
- **Arrange mode:** drag games, filter tabs and launchers into your own order. Alt+arrows nudge, Reset puts everything back A–Z.
- **Tags** with a one-click tag editor.
- **Wallpapers** per filter, with dim and blur. Adjustable card size, Mica window, start with Windows.

![Arrange mode: drag handles on every game, filter tab and launcher](docs/screenshots/arrange.jpg)

**Trainers**
- Put trainer `.exe`s in a `z` subfolder: they're matched to games by name and get a TRAINER badge. **Ctrl+Enter** starts the game, then the trainer after a delay you choose.

![Settings: card size and shape, wallpapers per filter, games folder and hotkey](docs/screenshots/settings.jpg)

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

Shortcuts themselves are never modified: the launcher only reads them.

## Where cover art comes from

| Source | Used for |
| --- | --- |
| Your own image | Always wins. Set per shape (landscape / portrait) from the tile's **Cover** menu. |
| Steam | Steam games: the app id from a `steam://` link, otherwise an exact name match. |
| Microsoft Store, GOG | Games Steam doesn't have, by exact name. Also searchable in **Find cover online…**. |

Epic doesn't allow outside apps to search its store, but many Epic games are also on the Microsoft Store and are found there.

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

Everything the launcher remembers lives next to the exe: `launcher-data.json` (settings, tags, trainer choices, play history, custom order), `covers\` and `wallpapers\`. To move it to another PC, copy the folder.

## Requirements

- Windows 11 (Windows 10 works without the Mica background), 64-bit
- To build from source: .NET 10 SDK. The release exe needs nothing extra; a build with `--self-contained false` needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Build and run

```powershell
dotnet run                                   # debug run
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o app
```

The published `app\GameLauncher.exe` is a single file. On first start it opens Settings so you can pick your games folder.

## Releasing

Push a version tag and GitHub Actions builds the self-contained exe and publishes the release:

```powershell
git tag v1.0.2
git push origin v1.0.2
```
