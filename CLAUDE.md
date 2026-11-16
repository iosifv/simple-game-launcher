# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**Simple Game Launcher** — a native Windows desktop app (C# / WPF, .NET 10) that shows a folder of game shortcuts (`.lnk` / `.url`) as a searchable grid of cover-art tiles. It is a personal tool: single window, tray icon, global hotkey, no network services besides fetching cover art from Steam.

## Build, run, publish

```powershell
dotnet build                       # Debug build → bin\Debug\net10.0-windows\GameLauncher.exe
dotnet run
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o app
```

- The app is **single-instance** (named mutex). Before rebuilding or republishing, stop the running copy: `Get-Process GameLauncher | Stop-Process -Force`, otherwise the exe is locked.
- There is no test project. Verify changes by running the app (see "Verifying UI changes").
- `dotnet` may not be on PATH in a fresh shell right after install; refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.

## Project layout

| File | Responsibility |
| --- | --- |
| `App.xaml(.cs)` | Startup, single-instance mutex + "show" event, tray icon (WinForms `NotifyIcon`), unhandled-exception log (`error.log`), `ExitApp`. Merges the WPF **Fluent dark** theme. |
| `MainWindow.xaml` | The whole UI: header (icon + search + buttons + own caption buttons), launcher bar, filter tabs + arrange bar, games grid, status bar, wallpaper layer, drag canvas, settings drawer. Styles/templates live in `Window.Resources`. |
| `MainWindow.xaml.cs` | All behavior, grouped by `// ---------- section ----------` comments: lifecycle/hotkey, folder watching + rescan, art loading, filtering/sorting, keyboard, launching, context menu, arrange mode (drag & drop), card size, wallpapers, settings panel, title bar. |
| `Models.cs` | `GameItem`, `LauncherItem`, `TabVm`, `WallpaperRow` (view models, `Notify` base), `GameData` + `Settings` (persisted), `Store` (load/save JSON), `StartupReg` (HKCU Run key). |
| `Library.cs` | Pure file-system scan of the shortcuts folder (`Scan`), name normalization, initials, trainer-name parsing, keys. Safe off the UI thread. |
| `Covers.cs` | Cover art sources and cache: Steam (store search + `IStoreBrowseService` asset URLs), Microsoft Store and GOG search, image loading, glow colour (`VividColor`) and icon backdrop. |
| `CoverPicker.cs` | Code-built "Find cover online…" window: searches all stores, lets the user pick a landscape and a portrait image separately. |
| `Native.cs` | Win32/DWM interop: Mica, dark title bar, removing `WS_SYSMENU`, hotkey parsing/registration, shell icons via `IShellItemImageFactory` on a dedicated STA thread. |
| `InputDialog.cs` | Tiny code-built text prompt (used for tags). |
| `DarkMenu.cs` | `DarkMenuRenderer` / `DarkMenuColors`: paints the WinForms tray menu dark (WinForms ignores the WPF theme). |
| `tools/make-icon.ps1` | Regenerates `app.ico` (gamepad glyph, multi-size PNG ICO). |

## Key concepts

- **Folder conventions** (inside the chosen games folder): root shortcuts = games; `Launchers\` = launcher bar; `z\` = trainers (files containing "trainer"); `ICOS\` ignored; any other subfolder = its own filter tab. Folders listed in `Settings.NonGameFolders` (default `Tools`) are excluded from the **Games** filter.
- **Keys**: every game is identified by its path *relative to the games folder*, without extension (e.g. `Tools\AHK`). All persisted per-game data and the custom order use these keys, so data survives moving the whole folder. Renames are migrated in `MigrateKeys` / `Store.Move` / `Store.MoveOrderKey`.
- **Persistence**: `launcher-data.json` next to the exe (`AppContext.BaseDirectory`), plus `covers\` and `wallpapers\`. Writes are debounced via `SaveSoon()`. Mutate `Store.S` only on the UI thread. These files are personal and git-ignored.
- **Live updates**: a `FileSystemWatcher` on the games folder debounces into `Rescan()`, which scans off-thread and merges into the `Games` `ObservableCollection` in `Apply()` (existing `GameItem`s are reused so icons/covers don't reload).
- **Sorting**: `GameSorter` (a `ListCollectionView.CustomSort`) — search score when searching, last played on the Recent tab, otherwise `Settings.CustomOrder` index then file name. In **arrange mode** `CustomSort` is set to `null` and the `Games` collection order itself is edited with `Games.Move(...)`; `Done` writes it back to `CustomOrder` (an empty list means alphabetical). Filter tabs can be dragged in arrange mode too (`TabsList_PreviewMouse*`); their order is `Settings.TabOrder` (empty = default order from `DefaultTabs()`), applied in `OrderedTabs()`. Launcher-bar pills drag the same way (`LaunchersBar_PreviewMouse*`, clicks don't launch while arranging); their order is `Settings.LauncherOrder` (shortcut file names, empty = A–Z), applied in `OrderLaunchers()`.
- **Card look**: `Settings.CardLayout` (`landscape` / `portrait`) sets the art aspect in `ApplyTileSize`; it is changed by `SetCardLayout` (from the settings radios or `ShapeBtn` beside Arrange in the filter row, which `UpdateShapeBtn` keeps showing the current shape); `Settings.ShowCardText` is applied by `ApplyCardLook()`, which swaps window resources (`CardTextVis`, `NameOverlayVis`, `ArtCorner`, …) that the tile template reads via `DynamicResource`.
- **Trainers**: `Library.TrainerKey` strips version/"Plus N"/"Trainer" suffixes; exact normalized-name matches win, then prefix matches. `GameData.Trainer`: `null` = auto, `""` = none, path = manual.
- **Covers**: custom cover > Steam app id from `.url` > Steam store search by exact normalized name (result cached in `GameData.SteamLookupDone/SteamAppId`). `NoCover` forces icon-only. Each game has two custom slots, `CustomCover` (landscape) and `CustomCoverPortrait`, set separately from the tile's Cover menu. `LoadArt` order: custom image of the card's shape > Steam art of that shape (`header.jpg` / `library_600x900` → `steam_{id}_portrait.jpg`) > Steam header (portrait only). The two custom images are independent — a shape never shows the other shape's custom image (the user wants different pictures per shape). A cover whose aspect doesn't match the card (`GameItem.CoverFits = false`) is shown whole over a blurred backdrop (`CoverFill`, a 24 px decode stretched up). Newer Steam apps keep art under hashed paths, so fixed `…/apps/{id}/…jpg` URLs 404; `Covers.GetAssets` gets the real ones. Games Steam doesn't have fall back to an exact-name Microsoft Store / GOG match (`GameData.StoreLookupDone`, cached as `covers\store_*.jpg`). Epic blocks keyless requests. Cards without a cover show `GameItem.IconBackdrop`, a radial glow in the icon's colour.
- **Tile hover effect** (`// ---------- tile hover effect ----------`): code-behind animations on `MouseEnter`/`MouseLeave` (the whole card scales + lifts; the image inside does not zoom, by request; light sweep via the `Shine` border, glow in the cover's colour, and the `PlayBtn` play button pops in; Ctrl+click plays with the trainer). The `DropShadowEffect` is added only while hovered and removed after fading, to keep scrolling fast. The `WrapPanel` margin gives edge cards room to grow; keep it in sync with `PanelPadX` in `ApplyTileSize`.
- **Card spacing** is `Settings.CardSpacing` (px between cards): `ApplyTileSize` sets the `TileMargin` resource to half of it and uses it in the column maths. **Scroll by row** (`Settings.ScrollByRow`) makes the wheel glide to the next row boundary (`ScrollRows` / `ScrollStep` on `CompositionTarget.Rendering`).
- **Random game**: the header dice (`DiceBtn`, drawn in the `DiceBrush` resource from `Settings.DiceColor`) runs `RollDice`: selection hops across on-screen cards, then lands on a random game of the current filter; `Settings.DiceStartsGame` also starts it.
- **Trainers folder `z`**: `.exe` files need "trainer" in the name; any `.lnk` / `.url` shortcut there counts. `StartProcess` leaves the working directory to a shortcut's own "Start in".

## Gotchas

- **Caption buttons**: the window extends glass into the client area (`WindowChrome GlassFrameThickness=-1`) for Mica. DWM would then draw the system min/max/close under our UI (doubled, or hidden by wallpapers), so `Native.RemoveSysMenu` strips `WS_SYSMENU` and the app draws its own buttons. `WndProc` answers `WM_NCHITTEST` with `HTMAXBUTTON` over `MaxBtn` so the Windows 11 **Snap Layouts** flyout still works, and handles `WM_NCLBUTTONUP` to maximize. Keep this if you touch the header.
- Elements in the 52 px header that must be clickable need `WindowChrome.IsHitTestVisibleInChrome="True"`.
- **Fluent theme** comes from `PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml`. The Fluent `TextBox` shows its own clear (×) button when it has text — the search hint is hidden then to avoid overlap.
- **Keyboard routing**: `OnPreviewKeyDown` / `OnPreviewTextInput` on the window send typing to the search box. Anything new that takes text input must be excluded there (see the settings-panel and `TextBox` checks), and Ctrl shortcuts must not leak characters into search.
- `ImplicitUsings` is **disabled** and both WPF and WinForms are enabled — use explicit `using`s and fully qualify WinForms types (`WinForms.NotifyIcon`) to avoid ambiguity.
- Steam/fandom images may arrive as WebP; WPF decodes WebP via WIC on Windows 11, `System.Drawing` does not.

## Verifying UI changes

There are no automated tests. After a change: build, run the Debug exe, and check it visually. Useful techniques from past sessions:
- Screenshot the window with `Graphics.CopyFromScreen` over `GetWindowRect` (call `SetProcessDPIAware` first).
- Drive the UI with UI Automation (`AutomationId` = `x:Name`, e.g. `ArrangeBtn`, `SearchBox`, `GamesList`) or `WScript.Shell.SendKeys`; simulate drags with `SetCursorPos` + `mouse_event`.
- Check `bin\Debug\net10.0-windows\error.log` for unhandled exceptions.
- The Debug build keeps its own `launcher-data.json` in `bin\` — safe to experiment on.

## Conventions

- Keep the app dependency-free (no NuGet packages so far); Win32 interop goes in `Native.cs`.
- UI text is sentence case and short; dark theme colors come from the `TextBrush` / `SecondaryBrush` / `MutedBrush` / `AccentBrush` resources in `App.xaml`.
- Never modify the user's shortcut files; the launcher only reads them.
