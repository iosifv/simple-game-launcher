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
- **Top fade**: once the grid is scrolled, `UpdateTopFade` (on `ScrollViewer.ScrollChangedEvent`) puts an absolute-mapped gradient `OpacityMask` on `GamesList` so cards fade out under the filter row; it grows with the first `TopFadeHeight` px of scrolling and is removed at offset 0.
- **Card spacing** is `Settings.CardSpacing` (px between cards): `ApplyTileSize` sets the `TileMargin` resource to half of it and uses it in the column maths. **Scroll by row** (`Settings.ScrollByRow`) makes the wheel glide to the next row boundary (`ScrollRows` / `ScrollStep` on `CompositionTarget.Rendering`).
- **Random game**: the header dice (`DiceBtn`, drawn in the `DiceBrush` resource from `Settings.DiceColor`) runs `RollDice`: selection hops across on-screen cards, then lands on a random game of the current filter; `Settings.DiceStartsGame` also starts it.
- **Trainers on/off** (`Settings.TrainersEnabled`): when off, `AssignTrainers` clears every `GameItem.Trainer`, so `HasTrainer` is false everywhere (no badges, no "With trainer" tab from `DefaultTabs()`, Ctrl+Enter plays normally); `ApplyTrainerUi` hides the trainer settings row (`TrainerVis` resource) and search hint, and `Tile_ContextMenuOpening` hides the trainer menu items.
- **Window placement** (`// ---------- window placement ----------`): multi-monitor aware via Win32 `GetWindowPlacement` / `SetWindowPlacement` (`Native.GetNormalBounds` / `SetNormalBounds`). `Settings.WindowBounds` = normal bounds in real screen pixels (identifies the monitor, even with mixed scaling), `Settings.Maximized` = state; saved on move / resize / state change and on close. `ApplySavedPlacement` runs in `OnSourceInitialized` (handle exists, nothing shown yet), sets the bounds only if the title bar lies on a connected monitor (`MonitorFromRect`), then maximizes on that monitor if needed; otherwise the window opens centred at `Settings.Width/Height`. Don't go back to WPF `Left`/`Top` (DIPs drift between monitors with different scaling).
- **Look** (`// ---------- look: … ----------`, Settings → Look, each its own switch): the *game in focus* is the hovered card (`_hoverGame`, cleared only when the pointer leaves `GamesList`), else the selection; `FocusSoon` debounces into `UpdateFocusLook`.
  - `Settings.HeroBackground`: `HeroLayer` (slots `HeroA` / `HeroB` cross-faded by `ShowHero(banner, ambient)`) sits above the per-filter wallpaper. `HeroImagesFor` picks the banner: the user's own background art (`GameData.CustomHero`, set from the tile's Cover menu → "Choose background art…" / reset with "Use automatic background art", items shown only while the setting is on; any size, wins even over `NoCover`), else the first picture at least `MinBannerPixels` wide of Steam's `library_hero` (`Covers.EnsureSteamHero`, `covers\steam_{id}_hero.jpg`), for non-Steam games the Microsoft Store's logo-free SuperHeroArt at 1920 px or GOG's horizontal cover (`Covers.EnsureStoreHero`, `covers\store_{key}_hero.jpg`), the custom landscape cover, the store landscape cover, the Steam header. Images load via `Covers.LoadImageAtMost` (never upscaled, so `PixelWidth` = real sharpness). Nothing sharp enough = no banner, only the ambient wash of the 24 px `CoverFill` — never a stretched blur in the banner. `HeroScrim` (placed in `UpdateBannerLayout`) darkens just behind the name and the filter row. Each slot has a dark ambient copy over the whole window (`HeroAmb*`) and the whole artwork as a clear banner across the top (`HeroBan*`, fading out via the `BannerFade` mask). `UpdateBannerLayout` sizes the banner (width / 3.1, the hero aspect) and grows `HeroInfo` so about the top three quarters of it sit above the filters. `HeroSoftShade` darkens bright art by its measured `Brightness` so text stays readable (the filter-row chips also have a dark tint for this). `HeroInfo` (fixed height, under the launcher bar) shows the name.
  - `Settings.AccentFromGame`: `MainWindow` has its own `AccentBrush` resource (shadows App's) and **every use in MainWindow.xaml must be `DynamicResource`**. Styles freeze brushes, so `AccentTick` swaps in a new frozen brush per frame instead of animating one. `ReadableAccent` keeps it bright; `GlowColor` falls back to `DefaultAccent`, not the window accent.
  - `Settings.Spotlight`: `Spotlight(item)` fades every other card's `ListBoxItem` to `SpotlightOpacity` (1 − `Settings.SpotlightDim` %, the "Dim others" slider, 10–90) on hover; `Spotlight(null)` restores them (leaving the grid, entering arrange mode, window deactivated). Don't use the `Card` opacity for it — dragging uses that.
  - `Settings.CompactHeader`: `ApplyHeaderLook` hides `AppTitle`, `RefreshBtn`, `FolderBtn`, `SettingsBtn` and shows `MoreBtn` (⋯ menu: settings, rescan, open folder, exit). All three off = the original look.
- **Card info line** (`GameItem.SubText`, also the banner's `HeroSub`): "Played X ago" · store · folder · tags, each only when known. The store (`GameItem.StoreName`) comes from `Library.StoreOf` during the scan: a `.url`'s text, or the strings inside a `.lnk` read as ANSI and UTF-16 (`LinkText`), matched against the `Stores` markers (install folders, `xxx://` links, a Store app id); a Steam app id always means Steam. Standalone `.exe` shortcuts get no store, and the line is then empty. The folder name is left out while that folder's tab is open (`GameItem.OpenFolder`, set in `RefreshView`). Play time isn't tracked on purpose: games started through Steam/Epic relaunch themselves, so it would be wrong.
- **Tag filters** show a tag icon (`TabVm.IsTag`, `WallpaperRow.IsTag`) instead of a "#" in the label.
- **Hidden games** (`GameData.Hidden` → `GameItem.Hidden`, toggled by the tile menu's Hide / Show again via `SetHidden`): `Matches` drops them from every filter, search and so the dice; only the `hidden` tab (added last by `DefaultTabs()` while anything is hidden, crossed-out-eye icon via `IsHidden`) lists them. Folder / tag / trainer tabs are built from non-hidden games only. The file is never touched.
- **Hover tips** (`Settings.ShowTips`): one class handler on `FrameworkElement.ToolTipOpeningEvent`, registered in `App.OnStartup`, cancels every tooltip in every window when off — new tooltips need no extra code.
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
- Never modify the user's shortcut files; the launcher only reads them. The one exception is the user-requested **Delete…** menu item (game tiles and launcher pills): `DeleteShortcut` asks first (`InputDialog.ConfirmDelete`, Cancel is the default) and moves just that file to the Recycle Bin (`Native.RecycleFile`), never deleting permanently. Keep it that way.
