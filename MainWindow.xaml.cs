using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;

namespace GameLauncher
{
    public partial class MainWindow : Window
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        const int HotkeyId = 0x4747;

        public ObservableCollection<GameItem> Games { get; } = new ObservableCollection<GameItem>();
        public ObservableCollection<LauncherItem> Launchers { get; } = new ObservableCollection<LauncherItem>();
        public ObservableCollection<TabVm> Tabs { get; } = new ObservableCollection<TabVm>();

        public bool AllowClose;

        readonly ListCollectionView _view;
        readonly DispatcherTimer _rescanTimer, _saveTimer, _statusTimer;
        static readonly SemaphoreSlim NetGate = new SemaphoreSlim(4);

        FileSystemWatcher _watcher;
        IntPtr _hwnd;
        bool _hotkeyOk, _rootExists = true, _scanning, _rescanAgain, _suppressTab, _dialogOpen, _loadingSettings;
        string _currentWallpaper;
        readonly Dictionary<string, ImageSource> _wallpaperCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        List<string> _trainers = new List<string>();

        internal string Query = "", QNorm = "", Tab = "all";

        public MainWindow()
        {
            InitializeComponent();
            RestoreWindowPlacement();
            Tab = Store.S.DefaultTab;

            ApplyCardLook();
            ApplyDiceColor();
            InitLook();

            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Games);
            _view.Filter = o => Matches((GameItem)o);
            _view.CustomSort = new GameSorter(this);
            GamesList.ItemsSource = _view;
            LaunchersBar.ItemsSource = Launchers;
            TabsList.ItemsSource = Tabs;

            _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _rescanTimer.Tick += (s, e) => { _rescanTimer.Stop(); _ = Rescan(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Store.Save(); };
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (s, e) => { _statusTimer.Stop(); UpdateStatus(); };
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScrollTimer.Tick += (s, e) => AutoScrollTick();

            SourceInitialized += OnSourceInitialized;
            StateChanged += (s, e) =>
            {
                bool max = WindowState == WindowState.Maximized;
                RootBorder.Margin = max ? new Thickness(7) : new Thickness(0);
                SaveWindowPlacement();
                MaxBtn.Content = max ? "" : "";
            };
            SizeChanged += (s, e) =>
            {
                // The app name in the header gives way to the search box on narrow windows.
                AppTitle.Visibility = !Store.S.CompactHeader && ActualWidth >= 960 ? Visibility.Visible : Visibility.Collapsed;
                SaveWindowPlacement();
            };
            LocationChanged += (s, e) => SaveWindowPlacement();
            Deactivated += (s, e) =>
            {
                Spotlight(null); // e.g. a game started from a card: don't come back with the grid dimmed
                if (Store.S.HideOnFocusLoss && !_dialogOpen && OwnedWindows.Count == 0 && !AnyContextMenuOpen()) Hide();
            };
        }

        // ---------- lifecycle ----------

        public void Initialize()
        {
            new WindowInteropHelper(this).EnsureHandle();
            StartWatching();
            _ = Rescan();
            InitUpdates();
            // First run (or the folder is gone): open settings so a folder can be chosen.
            if (string.IsNullOrWhiteSpace(Store.S.Folder) || !Directory.Exists(Store.S.Folder))
                Dispatcher.BeginInvoke(new Action(() => { if (SettingsPanel.Visibility != Visibility.Visible) ToggleSettings(); }),
                                       DispatcherPriority.ApplicationIdle);
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplySavedPlacement();
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook(WndProc);
            if (Native.TryMica(_hwnd))
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                RootBorder.Background = Brushes.Transparent;
            }
            else
            {
                RootBorder.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x21));
            }
            // Without WS_SYSMENU, DWM stops drawing its own caption buttons under ours (they'd double up or hide under wallpapers).
            Native.RemoveSysMenu(_hwnd);
            RegisterHotkey();
        }

        void RegisterHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyOk = Native.ParseHotkey(Store.S.Hotkey, out var mods, out var vk)
                        && Native.RegisterHotKey(_hwnd, HotkeyId, mods | 0x4000 /* MOD_NOREPEAT */, vk);
            UpdateStatus();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
                      WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;
            switch (msg)
            {
                case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                    ToggleLauncher();
                    handled = true;
                    break;

                // Snap Layouts: Windows 11 shows the layout flyout when the maximize button reports HTMAXBUTTON.
                case WM_NCHITTEST:
                    if (IsOverMaxButton(lParam)) { handled = true; return new IntPtr(HTMAXBUTTON); }
                    break;
                case WM_NCMOUSEMOVE:
                    SetMaxHover(wParam.ToInt32() == HTMAXBUTTON);
                    break;
                case WM_NCMOUSELEAVE:
                    SetMaxHover(false);
                    break;
                case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                    handled = true;
                    break;
                case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                    handled = true;
                    Max_Click(null, null);
                    break;
            }
            return IntPtr.Zero;
        }

        bool IsOverMaxButton(IntPtr lParam)
        {
            if (!MaxBtn.IsVisible) return false;
            int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
            try
            {
                var p = MaxBtn.PointFromScreen(new Point(x, y));
                return p.X >= 0 && p.Y >= 0 && p.X < MaxBtn.ActualWidth && p.Y < MaxBtn.ActualHeight;
            }
            catch { return false; }
        }

        void SetMaxHover(bool on)
        {
            if (on) MaxBtn.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            else MaxBtn.ClearValue(BackgroundProperty);
        }

        // ---------- window placement ----------
        // The window comes back on the same monitor, at the same size, maximized or not. Saved with Windows' own
        // placement (Settings.WindowBounds = normal bounds in real screen pixels, which identifies the monitor even
        // when monitors use different scaling) plus Settings.Maximized. Restored in OnSourceInitialized, before the
        // window is first shown, so it never appears in the wrong place first. If that monitor is gone, the window
        // opens centred on the main screen at the last size (Settings.Width/Height).

        void RestoreWindowPlacement()
        {
            Width = Math.Max(MinWidth, Store.S.Width);
            Height = Math.Max(MinHeight, Store.S.Height);
        }

        /// <summary>Called from OnSourceInitialized: the window handle exists but nothing is on screen yet.</summary>
        void ApplySavedPlacement()
        {
            var s = Store.S;
            if (Native.SetNormalBounds(_hwnd, s.WindowBounds))
                WindowStartupLocation = WindowStartupLocation.Manual; // keep WPF from re-centring it
            // Maximizes on the monitor of the normal bounds just set (or the main screen if they were unusable).
            if (s.Maximized) WindowState = WindowState.Maximized;
        }

        void SaveWindowPlacement()
        {
            if (!IsLoaded || _hwnd == IntPtr.Zero || WindowState == WindowState.Minimized) return;
            var s = Store.S;
            var bounds = Native.GetNormalBounds(_hwnd);
            if (bounds == null) return;
            s.WindowBounds = bounds;
            s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.Width = ActualWidth;   // fallback size if the monitor is gone next time
                s.Height = ActualHeight;
            }
            SaveSoon();
        }

        public void ShowLauncher()
        {
            if (!IsVisible) Show();
            // From the taskbar / hotkey: come back maximized if it was.
            if (WindowState == WindowState.Minimized) WindowState = Store.S.Maximized ? WindowState.Maximized : WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
            Native.SetForegroundWindow(_hwnd);
            SearchBox.Focus();
            SearchBox.SelectAll();
            if (GamesList.SelectedIndex < 0) SelectFirst();
        }

        public void ToggleLauncher()
        {
            if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide();
            else ShowLauncher();
        }

        public void ReloadSettings()
        {
            Store.Load();
            ApplyCardLook();
            ApplyTileSize();
            ApplyDiceColor();
            _orderIndex = null;
            RegisterHotkey();
            StartWatching();
            foreach (var g in Games) g.LastWrite = DateTime.MinValue; // forces icon/cover reload
            _ = Rescan();
            Flash("Settings reloaded");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveWindowPlacement();
            Store.Save(); // the debounced save may not get to run if the app exits now
            if (!AllowClose)
            {
                e.Cancel = true;
                if (Store.S.CloseToTray) Hide();
                else ((App)Application.Current).ExitApp();
            }
            base.OnClosing(e);
        }

        // ---------- folder watching ----------

        void StartWatching()
        {
            _watcher?.Dispose();
            _watcher = null;
            var root = Store.S.Folder;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            FileSystemEventHandler changed = (s, e) => Dispatcher.BeginInvoke(new Action(QueueRescan));
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                MigrateKeys(e.OldFullPath, e.FullPath);
                QueueRescan();
            }));
            _watcher.Error += (s, e) => Dispatcher.BeginInvoke(new Action(() => { StartWatching(); QueueRescan(); }));
            _watcher.EnableRaisingEvents = true;
        }

        void QueueRescan()
        {
            _rescanTimer.Stop();
            _rescanTimer.Start();
        }

        /// <summary>Keeps tags, trainer choice and play history when a shortcut or folder is renamed.</summary>
        void MigrateKeys(string oldPath, string newPath)
        {
            var root = Store.S.Folder;
            var oldKey = Library.KeyFor(root, oldPath);
            var newKey = Library.KeyFor(root, newPath);
            if (oldKey == null || newKey == null) return;

            if (Directory.Exists(newPath))
            {
                foreach (var k in Store.S.Games.Keys.Where(k => k.StartsWith(oldKey + "\\", OIC)).ToList())
                    Store.Move(k, newKey + k.Substring(oldKey.Length));
                foreach (var k in Store.S.CustomOrder.Where(k => k.StartsWith(oldKey + "\\", OIC)).ToList())
                    Store.MoveOrderKey(k, newKey + k.Substring(oldKey.Length));
                _orderIndex = null;
            }
            else if (Library.IsShortcut(oldPath) && Library.IsShortcut(newPath))
            {
                Store.Move(oldKey, newKey);
                _orderIndex = null;
            }
            SaveSoon();
        }

        async Task Rescan()
        {
            if (_scanning) { _rescanAgain = true; return; }
            _scanning = true;
            try
            {
                do
                {
                    _rescanAgain = false;
                    var root = Store.S.Folder;
                    var result = await Task.Run(() => Library.Scan(root));
                    Apply(result);
                } while (_rescanAgain);
            }
            finally { _scanning = false; }
        }

        void Apply(ScanResult r)
        {
            _rootExists = r.RootExists;

            var existing = Games.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in r.Games)
            {
                if (e.Key == null || !seen.Add(e.Key)) continue;
                var d = Store.Find(e.Key);
                if (!existing.TryGetValue(e.Key, out var g))
                {
                    g = new GameItem { Key = e.Key, LastWrite = DateTime.MinValue, Arranging = _arranging };
                    existing[e.Key] = g;
                    Games.Add(g);
                }
                bool changed = g.LastWrite != e.LastWrite || !string.Equals(g.FilePath, e.FilePath, OIC);
                g.FilePath = e.FilePath;
                g.SortKey = e.SortKey;
                g.SteamAppId = e.SteamAppId;
                g.StoreName = e.StoreName;
                g.Name = e.Name;
                g.Folder = e.Folder;
                g.Tags = d?.Tags?.ToList() ?? new List<string>();
                g.LastPlayed = d?.LastPlayed;
                g.Hidden = d?.Hidden == true;
                if (changed)
                {
                    g.LastWrite = e.LastWrite;
                    _ = LoadArt(g);
                }
            }
            foreach (var g in Games.Where(g => !seen.Contains(g.Key)).ToList()) Games.Remove(g);

            _trainers = r.Trainers;
            AssignTrainers();

            var oldLaunchers = Launchers.ToDictionary(l => l.FilePath, StringComparer.OrdinalIgnoreCase);
            var newLaunchers = OrderLaunchers(r.Launchers.Select(l =>
            {
                if (oldLaunchers.TryGetValue(l.FilePath, out var li) && li.LastWrite == l.LastWrite) return li;
                var item = new LauncherItem { Name = l.Name, FilePath = l.FilePath, LastWrite = l.LastWrite };
                _ = LoadLauncherIcon(item);
                return item;
            }));
            if (!newLaunchers.SequenceEqual(Launchers))
            {
                Launchers.Clear();
                foreach (var l in newLaunchers) Launchers.Add(l);
            }

            RebuildTabs();
            RefreshView(keepSelection: true);
        }

        void AssignTrainers()
        {
            ApplyTrainerUi();
            if (!Store.S.TrainersEnabled)
            {
                foreach (var g in Games) g.Trainer = null; // HasTrainer false everywhere: no badges, tab or menus
                return;
            }
            var candidates = _trainers
                .Select(t => (path: t, key: Library.TrainerKey(t)))
                .Where(x => x.key.Length > 0)
                .GroupBy(x => x.key)
                .Select(grp => grp.OrderBy(x => x.path.EndsWith(".exe", OIC) ? 0 : 1).First())
                .ToList();

            var exact = new Dictionary<GameItem, string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Games)
            {
                var m = candidates.FirstOrDefault(x => x.key == g.SearchKey);
                if (m.path != null) { exact[g] = m.path; used.Add(m.path); }
            }

            foreach (var g in Games)
            {
                var d = Store.Find(g.Key);
                if (d?.Trainer != null)
                {
                    g.Trainer = d.Trainer.Length > 0 && File.Exists(d.Trainer) ? d.Trainer : null;
                    continue;
                }
                if (exact.TryGetValue(g, out var p)) { g.Trainer = p; continue; }

                var k = g.SearchKey;
                var partial = k.Length >= 4
                    ? candidates.Where(x => !used.Contains(x.path) && (x.key.StartsWith(k) || k.StartsWith(x.key)))
                                .OrderByDescending(x => x.key.Length).FirstOrDefault()
                    : default;
                g.Trainer = partial.path;
            }
        }

        // ---------- art ----------

        async Task LoadArt(GameItem g)
        {
            int ver = ++g.ArtVersion;
            var icon = await Icons.GetAsync(g.FilePath, 128);
            if (ver != g.ArtVersion) return;
            g.Icon = icon;
            g.IconBackdrop = Covers.IconBackdrop(icon);

            var d = Store.Find(g.Key);
            bool portrait = Portrait;
            if (d?.NoCover == true) { SetCover(g, null, null); return; }

            // Order: custom image of the card's shape > Steam art of that shape > Steam landscape header (portrait cards only).
            // The landscape and portrait custom images are independent: one shape never borrows the other's.
            static string Existing(string p) => !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
            var sameCustom = Existing(portrait ? d?.CustomCoverPortrait : d?.CustomCover);
            if (sameCustom != null) { await ShowCover(g, ver, sameCustom); return; }

            int? id = g.SteamAppId ?? d?.SteamAppId;
            string steam = id == null ? null : Existing(portrait ? Covers.CachedPortraitPath(id.Value) : Covers.CachedPath(id.Value));

            if (steam == null && Store.S.FetchCovers)
            {
                await NetGate.WaitAsync();
                try
                {
                    if (id == null && d?.SteamLookupDone != true)
                    {
                        var (ok, found) = await Covers.SearchSteam(g.Name);
                        if (ok)
                        {
                            var gd = Store.Game(g.Key);
                            gd.SteamLookupDone = true;
                            gd.SteamAppId = found;
                            SaveSoon();
                            id = found;
                        }
                    }
                    if (id != null)
                        steam = portrait ? await Covers.EnsureSteamPortrait(id.Value) : await Covers.EnsureSteamCover(id.Value);
                }
                finally { NetGate.Release(); }
            }
            if (ver != g.ArtVersion) return;
            if (steam != null) { await ShowCover(g, ver, steam); return; }

            // Not on Steam: Microsoft Store / GOG art for an exact name match, cached per game.
            if (id == null && Store.Find(g.Key)?.SteamLookupDone == true)
            {
                string Other() => Existing(Covers.StoreCachedPath(g.Key, portrait)) ?? Existing(Covers.StoreCachedPath(g.Key, !portrait));
                var other = Other();
                if (other == null && Store.S.FetchCovers && Store.Find(g.Key)?.StoreLookupDone != true)
                {
                    await NetGate.WaitAsync();
                    try
                    {
                        if (await Covers.EnsureOtherStoreArt(g.Key, g.Name))
                        {
                            Store.Game(g.Key).StoreLookupDone = true;
                            SaveSoon();
                        }
                    }
                    finally { NetGate.Release(); }
                    if (ver != g.ArtVersion) return;
                    other = Other();
                }
                if (other != null) { await ShowCover(g, ver, other); return; }
            }

            if (portrait && id != null)
            {
                var header = Existing(Covers.CachedPath(id.Value));
                if (header == null && Store.S.FetchCovers)
                {
                    await NetGate.WaitAsync();
                    try { header = await Covers.EnsureSteamCover(id.Value); }
                    finally { NetGate.Release(); }
                }
                if (header != null) { await ShowCover(g, ver, header); return; }
            }
            if (ver == g.ArtVersion) SetCover(g, null, null);
        }

        async Task ShowCover(GameItem g, int ver, string path)
        {
            var (img, fill) = await Task.Run(() => (Covers.LoadImage(path), Covers.LoadImage(path, 24)));
            if (ver != g.ArtVersion) return;
            SetCover(g, img, fill);
            if (g == FocusGame) { _heroGame = null; FocusSoon(); } // its accent / hero can use the new art now
        }

        static void SetCover(GameItem g, ImageSource img, ImageSource fill)
        {
            g.Cover = img;
            g.CoverFill = fill;
            // Fits = roughly the card's aspect ratio; otherwise show it whole over a blurred backdrop.
            if (img == null || img.Height <= 0) { g.CoverFits = true; return; }
            double ratio = img.Width / img.Height, target = Portrait ? 2.0 / 3.0 : 460.0 / 215.0;
            g.CoverFits = Math.Abs(ratio - target) / target < 0.25;
        }

        void ReloadAllArt()
        {
            foreach (var g in Games) _ = LoadArt(g);
        }

        async Task LoadLauncherIcon(LauncherItem l) => l.Icon = await Icons.GetAsync(l.FilePath, 32);

        // ---------- filtering / sorting ----------

        bool Matches(GameItem g)
        {
            // Hidden games appear only under the Hidden filter (and searching there searches just them).
            if (Tab == "hidden") return g.Hidden && Score(g) > 0;
            if (g.Hidden) return false;
            if (QNorm.Length > 0) return Score(g) > 0; // search looks across everything
            if (Tab == "games") return IsGame(g);
            if (Tab == "recent") return g.LastPlayed != null;
            if (Tab == "trainer") return g.HasTrainer;
            if (Tab.StartsWith("folder:")) return string.Equals(g.Folder, Tab.Substring(7), OIC);
            if (Tab.StartsWith("tag:")) return g.Tags.Contains(Tab.Substring(4), StringComparer.OrdinalIgnoreCase);
            return true;
        }

        Dictionary<string, int> _orderIndex;

        /// <summary>Position in the custom order; games not in it sort after (alphabetically).</summary>
        internal int OrderOf(GameItem g)
        {
            if (_orderIndex == null)
            {
                _orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Store.S.CustomOrder.Count; i++) _orderIndex.TryAdd(Store.S.CustomOrder[i], i);
            }
            return _orderIndex.TryGetValue(g.Key, out var idx) ? idx : int.MaxValue;
        }

        static bool IsGame(GameItem g) =>
            string.IsNullOrEmpty(g.Folder) || !Store.S.NonGameFolders.Contains(g.Folder, StringComparer.OrdinalIgnoreCase);

        internal int Score(GameItem g)
        {
            if (QNorm.Length == 0) return 1;
            if (g.SearchKey.StartsWith(QNorm)) return 5;
            if (g.Initials.StartsWith(QNorm)) return 4;
            if (g.SearchKey.Contains(QNorm)) return 3;
            if (g.Tags.Any(t => Library.Normalize(t).StartsWith(QNorm))) return 2;
            if (Library.Normalize(g.Folder).StartsWith(QNorm)) return 1;
            return 0;
        }

        sealed class GameSorter : System.Collections.IComparer
        {
            readonly MainWindow _w;
            public GameSorter(MainWindow w) { _w = w; }
            public int Compare(object a, object b)
            {
                var x = (GameItem)a; var y = (GameItem)b;
                if (_w.QNorm.Length > 0)
                {
                    int s = _w.Score(y).CompareTo(_w.Score(x));
                    if (s != 0) return s;
                }
                else if (_w.Tab == "recent")
                {
                    int c = Nullable.Compare(y.LastPlayed, x.LastPlayed);
                    if (c != 0) return c;
                }
                else
                {
                    int o = _w.OrderOf(x).CompareTo(_w.OrderOf(y));
                    if (o != 0) return o;
                }
                return string.Compare(x.SortKey, y.SortKey, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        void RefreshView(bool keepSelection)
        {
            var sel = GamesList.SelectedItem;
            var openFolder = Tab.StartsWith("folder:") ? Tab.Substring(7) : null;
            if (!string.Equals(openFolder, GameItem.OpenFolder, OIC))
            {
                GameItem.OpenFolder = openFolder; // the cards of that folder leave its name out of their info line
                foreach (var g in Games) if (g.Folder != null) g.RefreshSubText();
            }
            _view.Refresh();
            if (keepSelection && sel != null && _view.Contains(sel)) GamesList.SelectedItem = sel;
            else SelectFirst();
            UpdateStatus();
        }

        List<TabVm> DefaultTabs()
        {
            var list = new List<TabVm> { new TabVm("Games", "games"), new TabVm("All", "all"), new TabVm("Recent", "recent") };
            var shown = Games.Where(g => !g.Hidden).ToList(); // no filter that would list only hidden games
            if (shown.Any(g => g.HasTrainer)) list.Add(new TabVm("With trainer", "trainer"));
            foreach (var f in shown.Select(g => g.Folder).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f))
                list.Add(new TabVm(f, "folder:" + f));
            foreach (var t in shown.SelectMany(g => g.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t))
                list.Add(new TabVm(t, "tag:" + t)); // shown with a tag icon (TabStyle)
            if (Games.Any(g => g.Hidden)) list.Add(new TabVm("Hidden", "hidden"));
            return list;
        }

        /// <summary>Default tabs in the saved custom order (while arranging: the order on screen). New tabs go to the end.</summary>
        List<TabVm> OrderedTabs()
        {
            var list = DefaultTabs();
            var order = _arranging ? Tabs.Select(t => t.Value).ToList() : Store.S.TabOrder;
            if (order.Count == 0) return list;
            int Pos(TabVm t)
            {
                int i = order.FindIndex(v => string.Equals(v, t.Value, OIC));
                return i >= 0 ? i : order.Count + list.IndexOf(t);
            }
            return list.OrderBy(Pos).ToList();
        }

        void RebuildTabs()
        {
            var list = OrderedTabs();
            if (!list.SequenceEqual(Tabs))
            {
                _suppressTab = true;
                Tabs.Clear();
                foreach (var t in list) Tabs.Add(t);
                _suppressTab = false;
            }
            var sel = Tabs.FirstOrDefault(t => t.Value.Equals(Tab, OIC)) ?? Tabs[0];
            _suppressTab = true;
            TabsList.SelectedItem = sel;
            _suppressTab = false;
            Tab = sel.Value;
            ApplyWallpaper();
            if (SettingsPanel.Visibility == Visibility.Visible) BuildWallpaperRows();
        }

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTab || TabsList.SelectedItem is not TabVm t) return;
            Tab = t.Value;
            if (SearchBox.Text.Length > 0) SearchBox.Clear(); // also refreshes
            else RefreshView(keepSelection: false);
            ApplyWallpaper();
            foreach (var r in WallpaperRows.Items.OfType<WallpaperRow>()) r.IsCurrent = r.Key.Equals(Tab, OIC);
            if (SettingsPanel.Visibility != Visibility.Visible) SearchBox.Focus();
        }

        void CycleTab(int dir)
        {
            if (Tabs.Count == 0) return;
            int i = (TabsList.SelectedIndex + dir + Tabs.Count) % Tabs.Count;
            TabsList.SelectedIndex = i;
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Query = SearchBox.Text.Trim();
            QNorm = Library.Normalize(Query);
            Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Visibility = Placeholder.Visibility;
            RefreshView(keepSelection: false);
        }

        void SelectFirst()
        {
            GamesList.SelectedIndex = GamesList.Items.Count > 0 ? 0 : -1;
            if (GamesList.SelectedItem != null) GamesList.ScrollIntoView(GamesList.SelectedItem);
        }

        void UpdateStatus()
        {
            if (StatusText == null) return;
            if (!_rootExists)
                StatusText.Text = $"Folder not found: {Store.S.Folder}  (Settings → Games folder)";
            else
            {
                var shown = Games.Where(g => !g.Hidden).ToList();
                int games = shown.Count(IsGame), other = shown.Count - games, hidden = Games.Count - shown.Count;
                StatusText.Text = $"{games} games" + (other > 0 ? $"  ·  {other} tools" : "")
                                  + (Store.S.TrainersEnabled ? $"  ·  {shown.Count(g => g.HasTrainer)} with trainers" : "")
                                  + (hidden > 0 ? $"  ·  {hidden} hidden" : "")
                                  + $"  ·  watching {Store.S.Folder}";
            }
            HotkeyText.Text = _hotkeyOk ? $"{Store.S.Hotkey}  show / hide" : $"Hotkey {Store.S.Hotkey} is taken — change it in settings";
            CountText.Text = _view.Count == Games.Count ? "" : $"{_view.Count} of {Games.Count}";

            EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = !_rootExists ? "Shortcuts folder not found"
                : QNorm.Length > 0 ? $"No games match \"{Query}\""
                : Tab == "recent" ? "Games you launch from here will show up in Recent"
                : Tab == "hidden" ? "Nothing is hidden"
                : "Drop a shortcut into the folder and it appears here";
        }

        void Flash(string message)
        {
            StatusText.Text = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        void SaveSoon()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ---------- keyboard ----------

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool inSearchWithText = SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0;
            bool settingsOpen = SettingsPanel.Visibility == Visibility.Visible;

            if (ctrl && e.Key == Key.OemComma) { ToggleSettings(); e.Handled = true; return; }
            if (settingsOpen && e.Key == Key.Escape) { ToggleSettings(); e.Handled = true; return; }
            if (settingsOpen && SettingsPanel.IsKeyboardFocusWithin) return; // let the settings controls have their keys

            if (_arranging)
            {
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (k == Key.Escape) { if (_dragging) EndDrag(false); else ExitArrange(false); e.Handled = true; return; }
                if (k == Key.Enter) { ExitArrange(true); e.Handled = true; return; }
                if (alt && k == Key.Left) { Nudge(-1); e.Handled = true; return; }
                if (alt && k == Key.Right) { Nudge(1); e.Handled = true; return; }
                if (alt && k == Key.Up) { Nudge(-Columns()); e.Handled = true; return; }
                if (alt && k == Key.Down) { Nudge(Columns()); e.Handled = true; return; }
            }

            if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { ChangeTileSize(+20); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { ChangeTileSize(-20); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { ChangeTileSize(220 - Store.S.TileMinWidth); e.Handled = true; return; }

            switch (e.Key)
            {
                case Key.Escape:
                    if (SearchBox.Text.Length > 0) SearchBox.Clear(); else Hide();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (GamesList.SelectedItem is GameItem g) Launch(g, ctrl);
                    e.Handled = true;
                    break;
                case Key.Down: Move(Columns()); e.Handled = true; break;
                case Key.Up: Move(-Columns()); e.Handled = true; break;
                case Key.Left: if (!inSearchWithText) { Move(-1); e.Handled = true; } break;
                case Key.Right: if (!inSearchWithText) { Move(1); e.Handled = true; } break;
                case Key.Tab:
                    if (ctrl) { CycleTab(shift ? -1 : 1); e.Handled = true; }
                    break;
                case Key.F5: _ = Rescan(); Flash("Rescanned"); e.Handled = true; break;
            }
        }

        void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Typing anywhere goes to the search box.
            if (SearchBox.IsKeyboardFocused)
            {
                var m0 = Keyboard.Modifiers;
                if ((m0 & ModifierKeys.Control) != 0 && (m0 & ModifierKeys.Alt) == 0) e.Handled = true; // swallow text from Ctrl shortcuts
                return;
            }
            if (_arranging || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            if (Keyboard.FocusedElement is TextBox || SettingsPanel.IsKeyboardFocusWithin) return;
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0) return; // Ctrl shortcuts (AltGr = Ctrl+Alt still types)
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        void Move(int delta)
        {
            int n = GamesList.Items.Count;
            if (n == 0) return;
            int i = GamesList.SelectedIndex;
            i = i < 0 ? 0 : Math.Clamp(i + delta, 0, n - 1);
            GamesList.SelectedIndex = i;
            GamesList.ScrollIntoView(GamesList.SelectedItem);
        }

        /// <summary>Stretch tiles so each row fills the window width.</summary>
        void GamesList_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

        static bool Portrait => Store.S.CardLayout == "portrait";

        void ApplyTileSize()
        {
            // Portrait cards are narrower at the same "card size" so a similar number of games fits on screen.
            double minTile = Store.S.TileMinWidth * (Portrait ? 0.72 : 1);
            const double scrollbar = 18, PanelPadX = 12 + 10; // WrapPanel margin in MainWindow.xaml
            double margin = Store.S.CardSpacing;               // the gap between cards
            Resources["TileMargin"] = new Thickness(margin / 2);
            double avail = GamesList.ActualWidth - scrollbar - PanelPadX;
            if (avail <= 0) return;
            int cols = Math.Max(1, (int)(avail / (minTile + margin)));
            double tile = Math.Floor(avail / cols - margin);
            Resources["TileWidth"] = tile;
            Resources["ArtHeight"] = Math.Round(Portrait ? tile * 1.5 : tile * 215.0 / 460.0);
        }

        /// <summary>Names under the cards on/off: swaps the template resources.</summary>
        void ApplyCardLook()
        {
            UpdateShapeBtn();
            bool text = Store.S.ShowCardText;
            Resources["CardTextVis"] = text ? Visibility.Visible : Visibility.Collapsed;
            Resources["NameOverlayVis"] = text ? Visibility.Collapsed : Visibility.Visible;
            Resources["CardTipOn"] = !text;
            Resources["ArtCorner"] = text ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);
            Resources["IconMargin"] = text ? new Thickness(0) : new Thickness(0, 0, 0, 26);
        }

        int Columns()
        {
            var gen = GamesList.ItemContainerGenerator;
            if (gen.ContainerFromIndex(0) is not FrameworkElement first) return 1;
            double y0 = first.TranslatePoint(new Point(), GamesList).Y;
            int cols = 0;
            for (int i = 0; i < GamesList.Items.Count; i++)
            {
                if (gen.ContainerFromIndex(i) is not FrameworkElement c) break;
                if (Math.Abs(c.TranslatePoint(new Point(), GamesList).Y - y0) > 1) break;
                cols++;
            }
            return Math.Max(1, cols);
        }

        // ---------- launching ----------

        void Launch(GameItem g, bool withTrainer)
        {
            if (_arranging) return; // no accidental launches while arranging
            if (!StartProcess(g.FilePath, out var error))
            {
                MessageBox.Show(this, $"Couldn't start {g.Name}.\n\n{error}", "Simple Game Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var d = Store.Game(g.Key);
            d.LastPlayed = DateTime.Now;
            d.PlayCount++;
            g.LastPlayed = d.LastPlayed;
            SaveSoon();

            if (withTrainer && g.HasTrainer)
            {
                var trainer = g.Trainer;
                int delay = Math.Max(0, Store.S.TrainerDelaySeconds);
                Flash($"Started {g.Name}  ·  trainer starts in {delay}s");
                Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!StartProcess(trainer, out var err) && err != null) Flash($"Trainer didn't start: {err}");
                })));
            }
            else
            {
                Flash($"Started {g.Name}");
            }

            if (Tab == "recent") RefreshView(keepSelection: true);
            if (Store.S.HideOnLaunch) Hide();
        }

        static bool StartProcess(string path, out string error)
        {
            error = null;
            try
            {
                // Shortcuts carry their own "Start in" folder (tools like SC2 Coop Overlay need it), so only
                // programs get their own folder as the working directory.
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Library.IsShortcut(path) ? "" : Path.GetDirectoryName(path) ?? ""
                });
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false; // user cancelled the UAC prompt
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        void Tile_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && (sender as FrameworkElement)?.DataContext is GameItem g) Launch(g, false);
        }

        void TrainerBadge_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is GameItem g) Launch(g, true);
        }

        void Launcher_Click(object sender, RoutedEventArgs e)
        {
            if (_arranging) return; // pills are being reordered, not started
            if ((sender as FrameworkElement)?.DataContext is LauncherItem l)
            {
                if (StartProcess(l.FilePath, out var err)) Flash($"Opened {l.Name}");
                else if (err != null) Flash($"Couldn't open {l.Name}: {err}");
            }
        }

        // ---------- context menu ----------

        void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.DataContext is not GameItem g) return;
            var root = Store.S.Folder;
            switch (mi.Tag as string)
            {
                case "play": Launch(g, false); break;
                case "playtrainer": Launch(g, true); break;

                case "settrainer":
                {
                    var z = Path.Combine(root, "z");
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = $"Choose a trainer for {g.Name}",
                        Filter = "Programs and shortcuts|*.exe;*.lnk;*.url|All files|*.*",
                        InitialDirectory = Directory.Exists(z) ? z : root
                    };
                    if (ShowDlg(dlg))
                    {
                        Store.Game(g.Key).Trainer = dlg.FileName;
                        AfterTrainerChange();
                    }
                    break;
                }
                case "autotrainer": Store.Game(g.Key).Trainer = null; AfterTrainerChange(); break;
                case "notrainer": Store.Game(g.Key).Trainer = ""; AfterTrainerChange(); break;

                case "setcover:landscape":
                case "setcover:portrait":
                {
                    // Landscape and portrait each keep their own image.
                    bool tall = (string)mi.Tag == "setcover:portrait";
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = $"Choose a {(tall ? "portrait (tall, e.g. 600×900)" : "landscape (wide, e.g. 460×215)")} cover for {g.Name}",
                        Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*"
                    };
                    if (ShowDlg(dlg))
                    {
                        var ext = Path.HasExtension(dlg.FileName) ? Path.GetExtension(dlg.FileName) : ".jpg";
                        if (SetCustomCover(g, tall, ext, dest => File.Copy(dlg.FileName, dest, true)))
                            Flash((tall ? "Portrait" : "Landscape") + $" image set for {g.Name}"
                                  + (tall != Portrait ? $"  ·  shows when cards are {(tall ? "portrait" : "landscape")} (Settings → Cards)" : ""));
                        SaveSoon();
                        _ = LoadArt(g);
                    }
                    break;
                }
                case "setbg":
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = $"Choose background art for {g.Name} (wide, ideally 1920 px or more)",
                        Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*"
                    };
                    if (!ShowDlg(dlg)) break;
                    var d = Store.Game(g.Key);
                    var ext = Path.HasExtension(dlg.FileName) ? Path.GetExtension(dlg.FileName) : ".jpg";
                    var dest = Path.Combine(Store.CoverDir, $"custom_{Library.Normalize(g.Key)}_hero_{DateTime.Now.Ticks}{ext}");
                    try { File.Copy(dlg.FileName, dest, true); }
                    catch (Exception ex) { Flash("Couldn't save image: " + ex.Message); break; }
                    TryDelete(d.CustomHero);
                    d.CustomHero = dest;
                    SaveSoon();
                    RefreshHero(g);
                    Flash($"Background art set for {g.Name}");
                    break;
                }
                case "autobg":
                {
                    var d = Store.Game(g.Key);
                    TryDelete(d.CustomHero);
                    d.CustomHero = null;
                    SaveSoon();
                    RefreshHero(g);
                    Flash($"{g.Name} uses automatic background art again");
                    break;
                }
                case "findcover":
                {
                    var pick = CoverPicker.Show(this, g.Name);
                    if (pick == null) break;
                    var set = new List<string>();
                    if (pick.Landscape != null && SetCustomCover(g, false, ".jpg", dest => File.WriteAllBytes(dest, pick.Landscape))) set.Add("landscape");
                    if (pick.Portrait != null && SetCustomCover(g, true, ".jpg", dest => File.WriteAllBytes(dest, pick.Portrait))) set.Add("portrait");
                    if (set.Count == 2) Flash($"Landscape and portrait covers set for {g.Name}");
                    else if (set.Count == 1)
                        Flash((set[0] == "portrait" ? "Portrait" : "Landscape") + $" cover set for {g.Name}"
                              + ((set[0] == "portrait") != Portrait ? $"  ·  shows when cards are {set[0]} (Settings → Cards)" : ""));
                    SaveSoon();
                    _ = LoadArt(g);
                    break;
                }
                case "steamcover:landscape":
                case "steamcover:portrait":
                {
                    var d = Store.Game(g.Key);
                    if ((string)mi.Tag == "steamcover:portrait") { TryDelete(d.CustomCoverPortrait); d.CustomCoverPortrait = null; }
                    else { TryDelete(d.CustomCover); d.CustomCover = null; }
                    d.NoCover = false;
                    if (g.SteamAppId == null && d.SteamAppId == null)
                    {
                        // Look again, on Steam and the other stores.
                        d.SteamLookupDone = false;
                        d.StoreLookupDone = false;
                    }
                    SaveSoon();
                    _ = LoadArt(g);
                    break;
                }
                case "nocover":
                {
                    var d = Store.Game(g.Key);
                    TryDelete(d.CustomCover);
                    TryDelete(d.CustomCoverPortrait);
                    d.CustomCover = null;
                    d.CustomCoverPortrait = null;
                    d.NoCover = true;
                    SaveSoon();
                    _ = LoadArt(g);
                    break;
                }

                case "tags":
                {
                    var tags = InputDialog.AskTags(this, g.Name, g.Tags, Games.SelectMany(x => x.Tags));
                    if (tags != null)
                    {
                        Store.Game(g.Key).Tags = tags.Count > 0 ? tags : null;
                        g.Tags = tags;
                        SaveSoon();
                        RebuildTabs();
                        RefreshView(keepSelection: true);
                    }
                    break;
                }

                case "arrange":
                    GamesList.SelectedItem = g;
                    EnterArrange();
                    break;

                case "reveal":
                    Reveal(g.FilePath);
                    break;

                case "hide":
                    SetHidden(g, !g.Hidden);
                    break;

                case "delete":
                    if (DeleteShortcut(g.Name, g.FilePath)) Games.Remove(g); // the folder watcher's rescan confirms it
                    break;
            }
        }

        /// <summary>Hide a game from every filter (it moves to the "Hidden" filter) or bring it back. The shortcut
        /// file isn't touched.</summary>
        void SetHidden(GameItem g, bool hidden)
        {
            Store.Game(g.Key).Hidden = hidden;
            g.Hidden = hidden;
            SaveSoon();
            RebuildTabs(); // adds / removes the Hidden filter (leaving it when it empties)
            RefreshView(keepSelection: false);
            Flash(hidden ? $"Hid {g.Name}  ·  it's under the Hidden filter" : $"{g.Name} is back");
        }

        static void Reveal(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        /// <summary>The one place the launcher changes the shortcuts folder: after a confirmation, the file goes to
        /// the Recycle Bin (restorable). Saved data for the game (tags, covers, play history) is kept, so restoring
        /// the file brings everything back. True if it was deleted.</summary>
        bool DeleteShortcut(string name, string path)
        {
            if (!InputDialog.ConfirmDelete(this, name, path)) return false;
            var error = Native.RecycleFile(path);
            if (error != null)
            {
                Flash($"Couldn't delete {name}: {error}");
                return false;
            }
            Flash($"Deleted {name}  ·  it's in the Recycle Bin if you want it back");
            QueueRescan();
            return true;
        }

        void LauncherReveal_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LauncherItem l) Reveal(l.FilePath);
        }

        void LauncherDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LauncherItem l && DeleteShortcut(l.Name, l.FilePath))
                Launchers.Remove(l);
        }

        void AfterTrainerChange()
        {
            SaveSoon();
            AssignTrainers();
            RebuildTabs();
            RefreshView(keepSelection: true);
        }

        /// <summary>Replaces the game's custom landscape or portrait cover with a new file written by <paramref name="write"/>.</summary>
        bool SetCustomCover(GameItem g, bool tall, string ext, Action<string> write)
        {
            var d = Store.Game(g.Key);
            var dest = Path.Combine(Store.CoverDir, $"custom_{Library.Normalize(g.Key)}{(tall ? "_portrait" : "")}_{DateTime.Now.Ticks}{ext}");
            try { write(dest); }
            catch (Exception ex) { Flash("Couldn't save image: " + ex.Message); return false; }
            TryDelete(tall ? d.CustomCoverPortrait : d.CustomCover);
            if (tall) d.CustomCoverPortrait = dest; else d.CustomCover = dest;
            d.NoCover = false;
            return true;
        }

        static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path) && path.StartsWith(Store.CoverDir, StringComparison.OrdinalIgnoreCase)) File.Delete(path); } catch { }
        }

        bool AnyContextMenuOpen() =>
            GamesList.Items.Cast<object>()
                .Select(i => GamesList.ItemContainerGenerator.ContainerFromItem(i) as FrameworkElement)
                .Any(c => c?.ContextMenu?.IsOpen == true);

        bool ShowDlg(Microsoft.Win32.FileDialog dlg)
        {
            _dialogOpen = true;
            try { return dlg.ShowDialog(this) == true; }
            finally { _dialogOpen = false; }
        }

        // ---------- arrange mode (custom order) ----------

        const string DefaultPlaceholder = "Search games — just start typing";
        bool _arranging, _dragging, _endingDrag, _alphaReset;
        GameItem _dragItem;
        Point _dragStart, _grabOffset;
        List<GameItem> _preDragOrder;
        Image _ghost;
        readonly DispatcherTimer _autoScrollTimer;
        double _autoScrollDir;
        ScrollViewer _gamesScroll;

        void ArrangeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arranging) ExitArrange(true); else EnterArrange();
        }

        void ArrangeDone_Click(object sender, RoutedEventArgs e) => ExitArrange(true);
        void ArrangeCancel_Click(object sender, RoutedEventArgs e) => ExitArrange(false);

        void ArrangeReset_Click(object sender, RoutedEventArgs e)
        {
            ApplyCollectionOrder(Games.OrderBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase).ToList());
            _alphaReset = true;
            // Filters go back to their default order too.
            var def = DefaultTabs();
            _suppressTab = true;
            for (int i = 0; i < def.Count; i++)
            {
                int cur = Tabs.IndexOf(def[i]);
                if (cur >= 0 && cur != i) Tabs.Move(cur, i);
            }
            _suppressTab = false;
            // And launchers A–Z.
            var alpha = Launchers.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            for (int i = 0; i < alpha.Count; i++)
            {
                int cur = Launchers.IndexOf(alpha[i]);
                if (cur != i) Launchers.Move(cur, i);
            }
            Flash("Games and launchers A–Z, filters in default order  ·  click Done to keep it");
        }

        void EnterArrange()
        {
            if (_arranging) return;
            Spotlight(null);
            if (SettingsPanel.Visibility == Visibility.Visible) ToggleSettings();
            if (SearchBox.Text.Length > 0) SearchBox.Clear();

            // Put the collection itself in the current order, then let drags move items in it directly.
            var ordered = Games.OrderBy(OrderOf).ThenBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase).ToList();
            _arranging = true;
            _alphaReset = false;
            _view.CustomSort = null;
            ApplyCollectionOrder(ordered);
            foreach (var g in Games) g.Arranging = true;

            ArrangeBar.Visibility = Visibility.Visible;
            TabsRight.Visibility = Visibility.Collapsed;
            TabsList.Tag = "arranging";
            Resources["TabGripVis"] = Visibility.Visible;
            SearchBox.IsEnabled = false;
            Placeholder.Text = "Arrange mode — drag tiles, filters or launchers to reorder, then click Done";
            LaunchersBar.Cursor = Cursors.SizeAll;
            SearchHint.Visibility = Visibility.Collapsed;
            RefreshView(keepSelection: true);
            GamesList.Focus();
        }

        void ExitArrange(bool save)
        {
            if (!_arranging) return;
            if (_dragging) EndDrag(true);
            EndTabDrag();
            EndLauncherDrag();
            if (save)
            {
                Store.S.CustomOrder = _alphaReset ? new List<string>() : Games.Select(g => g.Key).ToList();
                _orderIndex = null;
                var tabOrder = Tabs.Select(t => t.Value).ToList();
                Store.S.TabOrder = tabOrder.SequenceEqual(DefaultTabs().Select(t => t.Value)) ? new List<string>() : tabOrder;
                var launcherOrder = Launchers.Select(LauncherKey).ToList();
                var alphabetical = Launchers.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Select(LauncherKey);
                Store.S.LauncherOrder = launcherOrder.SequenceEqual(alphabetical) ? new List<string>() : launcherOrder;
                SaveSoon();
            }
            _arranging = false;
            LaunchersBar.Cursor = null;
            ApplyLauncherOrder(); // on cancel this puts the launchers back in their saved order
            foreach (var g in Games) g.Arranging = false;
            _view.CustomSort = new GameSorter(this);
            RebuildTabs(); // on cancel this puts the filters back in their saved order

            ArrangeBar.Visibility = Visibility.Collapsed;
            TabsRight.Visibility = Visibility.Visible;
            TabsList.Tag = null;
            Resources["TabGripVis"] = Visibility.Collapsed;
            SearchBox.IsEnabled = true;
            Placeholder.Text = DefaultPlaceholder;
            SearchHint.Visibility = Placeholder.Visibility;
            RefreshView(keepSelection: true);
            Flash(save ? (_alphaReset ? "Order reset to A–Z" : "Custom order saved") : "Arrange changes discarded");
            SearchBox.Focus();
        }

        void ApplyCollectionOrder(IList<GameItem> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                int cur = Games.IndexOf(order[i]);
                if (cur >= 0 && cur != i) Games.Move(cur, i);
            }
        }

        /// <summary>Moves item directly before/after target in the underlying collection.</summary>
        void MoveRelative(GameItem item, GameItem target, bool after)
        {
            int from = Games.IndexOf(item), to = Games.IndexOf(target);
            if (from < 0 || to < 0 || from == to) return;
            int dest = after ? (from < to ? to : to + 1) : (from < to ? to - 1 : to);
            dest = Math.Clamp(dest, 0, Games.Count - 1);
            if (dest != from) { Games.Move(from, dest); _alphaReset = false; }
        }

        void Nudge(int delta)
        {
            if (GamesList.SelectedItem is not GameItem g) return;
            var visible = _view.Cast<GameItem>().ToList();
            int i = visible.IndexOf(g), j = Math.Clamp(i + delta, 0, visible.Count - 1);
            if (i < 0 || j == i) return;
            MoveRelative(g, visible[j], after: j > i);
            GamesList.SelectedItem = g;
            GamesList.ScrollIntoView(g);
        }

        void Tile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_arranging) { e.Handled = true; return; }
            // Cover actions apply to the card shape in use; say which.
            if ((sender as FrameworkElement)?.ContextMenu is not ContextMenu cm) return;
            var d = (sender as FrameworkElement).DataContext is GameItem g ? Store.Find(g.Key) : null;
            foreach (var top in cm.Items.OfType<MenuItem>())
            {
                if (top.Tag is "playtrainer" or "trainermenu")
                    top.Visibility = Store.S.TrainersEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (top.Header as string == "Cover")
                    foreach (var sub in top.Items.OfType<FrameworkElement>())
                        if (sub.Tag is "setbg" or "autobg")
                        {
                            sub.Visibility = Store.S.HeroBackground ? Visibility.Visible : Visibility.Collapsed;
                            if (sub.Tag is "autobg") sub.IsEnabled = !string.IsNullOrEmpty(d?.CustomHero);
                        }
                if (top.Tag is "hide" && (sender as FrameworkElement).DataContext is GameItem hg)
                {
                    top.Header = hg.Hidden ? "Show again" : "Hide";
                    if (top.Icon is TextBlock icon) icon.Text = hg.Hidden ? "" : ""; // eye / crossed-out eye
                }
            }
            foreach (var mi in cm.Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()))
            {
                if (mi.Tag is not string tag || !tag.Contains(':')) continue;
                bool portraitItem = tag.EndsWith(":portrait");
                // The shape the cards use right now is in bold.
                mi.FontWeight = portraitItem == Portrait ? FontWeights.SemiBold : FontWeights.Normal;
                if (tag.StartsWith("steamcover"))
                    mi.IsEnabled = !string.IsNullOrEmpty(portraitItem ? d?.CustomCoverPortrait : d?.CustomCover) || d?.NoCover == true;
            }
        }

        // ---------- tile hover effect ----------
        // In: the card (the whole rectangle; the image inside doesn't zoom) lifts and grows with a little spring,
        // a light sweeps across the art, a glow in the cover's colour fades in and a play button pops up.
        // Out: the card dips below its size and settles while the glow expands and dissolves. The glow
        // (a DropShadowEffect) exists only while a card is hovered or fading out, so scrolling stays fast.

        const double HoverScale = 1.06, HoverLift = -5, GlowBlur = 26, GlowOpacity = 0.7;

        void Tile_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not ListBoxItem item || _arranging || (item.DataContext as GameItem)?.IsDragging == true) return;
            if (item.Template.FindName("Card", item) is not Border card) return;
            Panel.SetZIndex(item, 1); // grow over the neighbours, glow included

            var (scale, lift) = CardTransforms(card);
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
            var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
            Animate(scale, ScaleTransform.ScaleXProperty, HoverScale, 340, spring);
            Animate(scale, ScaleTransform.ScaleYProperty, HoverScale, 340, spring);
            Animate(lift, TranslateTransform.YProperty, HoverLift, 340, spring);

            if (card.Effect is not DropShadowEffect glow || glow.IsFrozen)
            {
                card.Effect = glow = new DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 0,
                    Opacity = 0,
                    RenderingBias = RenderingBias.Performance
                };
            }
            // Ambient glow: the cover's own colour (set every time, the cover may have changed).
            glow.Color = GlowColor(item.DataContext as GameItem);
            Animate(glow, DropShadowEffect.BlurRadiusProperty, GlowBlur, 320, smooth);
            Animate(glow, DropShadowEffect.OpacityProperty, GlowOpacity, 320, smooth);

            ShowPlay(item, true);
            Sweep(item);

            _hoverGame = item.DataContext as GameItem; // hero background + accent follow the pointer
            FocusSoon();
            if (Store.S.Spotlight) Spotlight(item);
        }

        bool _spotlit;
        static double SpotlightOpacity => 1 - Math.Clamp(Store.S.SpotlightDim, 10, 90) / 100.0;

        void SpotlightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingSettings || !IsLoaded) return;
            Store.S.SpotlightDim = (int)Math.Round(e.NewValue);
            SpotlightText.Text = $"{Store.S.SpotlightDim}%";
            SaveSoon();
        }

        /// <summary>Spotlight (Settings.Spotlight): every other card fades a little while one is hovered; null brings
        /// them all back. Uses the ListBoxItem's opacity (the Card's is used while dragging).</summary>
        void Spotlight(ListBoxItem on)
        {
            if (on == null && !_spotlit) return;
            _spotlit = on != null;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var it in GamesList.Items)
                if (GamesList.ItemContainerGenerator.ContainerFromItem(it) is ListBoxItem c)
                    Animate(c, OpacityProperty, on == null || c == on ? 1 : SpotlightOpacity, on == null ? 320 : 240, ease);
        }

        /// <summary>The play button pops in (spring) on hover and shrinks away when the mouse leaves.</summary>
        static void ShowPlay(ListBoxItem item, bool on)
        {
            if (item.Template.FindName("PlayBtn", item) is not Button play) return;
            if (play.RenderTransform is not ScaleTransform s || s.IsFrozen)
                play.RenderTransform = s = new ScaleTransform(0.75, 0.75);
            play.IsHitTestVisible = on;
            var ease = on ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
                          : new CubicEase { EasingMode = EasingMode.EaseIn };
            int ms = on ? 300 : 180;
            Animate(play, OpacityProperty, on ? 1 : 0, ms, null);
            Animate(s, ScaleTransform.ScaleXProperty, on ? 1 : 0.75, ms, ease);
            Animate(s, ScaleTransform.ScaleYProperty, on ? 1 : 0.75, ms, ease);
        }

        void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GameItem g) return;
            GamesList.SelectedItem = g;
            Launch(g, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }

        void Tile_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not ListBoxItem item || item.Template.FindName("Card", item) is not Border card) return;
            var (scale, lift) = CardTransforms(card);

            // Dip just under normal size, then settle: a soft "drop back into the grid".
            var settle = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            settle.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new CubicEase { EasingMode = EasingMode.EaseIn }));
            settle.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle.Clone());
            Animate(lift, TranslateTransform.YProperty, 0, 380, new CubicEase { EasingMode = EasingMode.EaseInOut });

            ShowPlay(item, false);

            if (item.Template.FindName("Shine", item) is Border shine)
                Animate(shine, OpacityProperty, 0, 200, null);

            if (card.Effect is DropShadowEffect glow && !glow.IsFrozen)
            {
                // The glow spreads out while it fades, then the effect is removed.
                Animate(glow, DropShadowEffect.BlurRadiusProperty, GlowBlur * 1.8, 420, new CubicEase { EasingMode = EasingMode.EaseOut });
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (s, a) =>
                {
                    if (item.IsMouseOver || card.Effect != glow) return; // hovered again meanwhile
                    card.Effect = null;
                    Panel.SetZIndex(item, 0);
                };
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
            }
            else Panel.SetZIndex(item, 0);
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, object> GlowColors = new();

        /// <summary>Glow colour for a card: the cover's most vivid colour (from its tiny blurred copy), else the icon's,
        /// else the accent. Cached per image, so a new cover gets a new colour.</summary>
        static Color GlowColor(GameItem g)
        {
            var accent = DefaultAccent; // not the window's accent, which itself follows this colour
            var src = (g?.CoverFill ?? g?.Cover ?? g?.Icon) as System.Windows.Media.Imaging.BitmapSource;
            if (src == null) return accent;
            if (GlowColors.TryGetValue(src, out var cached)) return (Color)cached;
            var c = Covers.VividColor(src) ?? accent;
            GlowColors.AddOrUpdate(src, c);
            return c;
        }

        static void Animate(IAnimatable target, DependencyProperty dp, double to, int ms, IEasingFunction ease) =>
            target.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });

        /// <summary>The card's scale + lift transforms, created on first use (template-made ones may be frozen).</summary>
        static (ScaleTransform scale, TranslateTransform lift) CardTransforms(Border card)
        {
            if (card.RenderTransform is TransformGroup g && !g.IsFrozen && g.Children.Count == 2
                && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
                return (s, t);
            s = new ScaleTransform(1, 1);
            t = new TranslateTransform();
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = new TransformGroup { Children = { s, t } };
            return (s, t);
        }

        /// <summary>A soft diagonal band of light that crosses the art once.</summary>
        static void Sweep(ListBoxItem item)
        {
            if (item.Template.FindName("Shine", item) is not Border shine) return;
            if (shine.Background is not LinearGradientBrush band || band.IsFrozen)
            {
                band = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35),
                        new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65)
                    },
                    RelativeTransform = new TranslateTransform()
                };
                shine.Background = band;
            }
            var move = (TranslateTransform)band.RelativeTransform;
            var sweep = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(750)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            move.BeginAnimation(TranslateTransform.XProperty, sweep);
            move.BeginAnimation(TranslateTransform.YProperty, sweep.Clone());
            shine.BeginAnimation(OpacityProperty, null);
            shine.Opacity = 1;
        }

        ListBoxItem ContainerAt(Point p)
        {
            var hit = GamesList.InputHitTest(p) as DependencyObject;
            while (hit != null && hit is not ListBoxItem)
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            return hit as ListBoxItem;
        }

        void GamesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_arranging) return;
            var c = ContainerAt(e.GetPosition(GamesList));
            if (c?.DataContext is not GameItem g) return; // e.g. the scrollbar
            _dragItem = g;
            _dragStart = e.GetPosition(GamesList);
            _grabOffset = e.GetPosition(c);
            GamesList.SelectedItem = g;
            GamesList.Focus();
            GamesList.CaptureMouse();
            e.Handled = true;
        }

        void GamesList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_arranging || _dragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(true); return; }
            var p = e.GetPosition(GamesList);
            if (!_dragging)
            {
                if ((p - _dragStart).Length < 6) return;
                StartDrag();
            }
            MoveGhost(e.GetPosition(DragCanvas));
            _autoScrollDir = p.Y < 60 ? -1 : p.Y > GamesList.ActualHeight - 60 ? 1 : 0;
            if (_autoScrollDir != 0) _autoScrollTimer.Start(); else _autoScrollTimer.Stop();
            UpdateDropTarget(p);
        }

        void GamesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragItem == null) return;
            EndDrag(true);
            e.Handled = true;
        }

        void GamesList_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dragItem != null && !_endingDrag) EndDrag(true);
        }

        void StartDrag()
        {
            _dragging = true;
            _preDragOrder = Games.ToList();
            if (GamesList.ItemContainerGenerator.ContainerFromItem(_dragItem) is FrameworkElement c && c.ActualWidth > 0)
            {
                var dpi = VisualTreeHelper.GetDpi(c);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(c), null, new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(c.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(c.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(1.05, 1.05));
                tg.Children.Add(new RotateTransform(2));
                _ghost = new Image
                {
                    Source = rtb,
                    Width = c.ActualWidth,
                    Height = c.ActualHeight,
                    Opacity = 0.92,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = tg,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 10, Opacity = 0.6 }
                };
                DragCanvas.Children.Add(_ghost);
            }
            _dragItem.IsDragging = true;
        }

        void MoveGhost(Point p)
        {
            if (_ghost == null) return;
            Canvas.SetLeft(_ghost, p.X - _grabOffset.X);
            Canvas.SetTop(_ghost, p.Y - _grabOffset.Y);
        }

        void UpdateDropTarget(Point p)
        {
            var c = ContainerAt(p);
            if (c?.DataContext is GameItem target)
            {
                if (target == _dragItem) return;
                bool after = Mouse.GetPosition(c).X > c.ActualWidth / 2;
                MoveRelative(_dragItem, target, after);
                return;
            }
            // Empty space after the last tile → move to the end of what's shown.
            var visible = _view.Cast<GameItem>().ToList();
            if (visible.Count == 0) return;
            var last = visible[^1];
            if (last == _dragItem) return;
            if (GamesList.ItemContainerGenerator.ContainerFromItem(last) is not FrameworkElement lc) return;
            var lp = lc.TranslatePoint(new Point(), GamesList);
            if (p.Y > lp.Y + lc.ActualHeight || (p.Y >= lp.Y && p.X > lp.X + lc.ActualWidth))
                MoveRelative(_dragItem, last, after: true);
        }

        void AutoScrollTick()
        {
            if (!_dragging || _autoScrollDir == 0) { _autoScrollTimer.Stop(); return; }
            _gamesScroll ??= FindChild<ScrollViewer>(GamesList);
            if (_gamesScroll == null) return;
            _gamesScroll.ScrollToVerticalOffset(_gamesScroll.VerticalOffset + _autoScrollDir * 14);
            UpdateDropTarget(Mouse.GetPosition(GamesList));
        }

        void EndDrag(bool commit)
        {
            if (_endingDrag) return;
            _endingDrag = true;
            try
            {
                _autoScrollTimer.Stop();
                if (GamesList.IsMouseCaptured) GamesList.ReleaseMouseCapture();
                if (_dragging && _dragItem != null)
                {
                    if (!commit && _preDragOrder != null) ApplyCollectionOrder(_preDragOrder);
                    DragCanvas.Children.Clear();
                    _ghost = null;
                    _dragItem.IsDragging = false;
                    GamesList.SelectedItem = _dragItem;
                    GamesList.ScrollIntoView(_dragItem);
                }
                _dragging = false;
                _dragItem = null;
                _preDragOrder = null;
            }
            finally { _endingDrag = false; }
        }

        // Filter tabs: drag to reorder while arranging. A plain click still switches the filter.
        TabVm _tabDragItem;
        bool _tabDragging;
        Point _tabDragStart;

        ListBoxItem TabContainerAt(Point p)
        {
            var hit = TabsList.InputHitTest(p) as DependencyObject;
            while (hit != null && hit is not ListBoxItem)
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            return hit as ListBoxItem;
        }

        void TabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_arranging) return;
            if (TabContainerAt(e.GetPosition(TabsList))?.DataContext is not TabVm t) return;
            _tabDragItem = t;
            _tabDragStart = e.GetPosition(TabsList);
        }

        void TabsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_arranging || _tabDragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndTabDrag(); return; }
            var p = e.GetPosition(TabsList);
            if (!_tabDragging)
            {
                if ((p - _tabDragStart).Length < 5) return;
                _tabDragging = true;
                TabsList.CaptureMouse();
            }
            var c = TabContainerAt(p);
            if (c?.DataContext is not TabVm target || target == _tabDragItem) return;
            int from = Tabs.IndexOf(_tabDragItem), to = Tabs.IndexOf(target);
            if (from < 0 || to < 0) return;
            // Only swap once the pointer is past the target's middle, so tabs of different widths don't flicker back and forth.
            double x = Mouse.GetPosition(c).X;
            if (from < to && x < c.ActualWidth / 2) return;
            if (from > to && x > c.ActualWidth / 2) return;
            _suppressTab = true;
            Tabs.Move(from, to);
            TabsList.SelectedItem = _tabDragItem;
            _suppressTab = false;
        }

        void TabsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_tabDragging) e.Handled = true; // a drag isn't a click
            EndTabDrag();
        }

        void TabsList_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_tabDragging) EndTabDrag();
        }

        void EndTabDrag()
        {
            bool was = _tabDragging;
            _tabDragging = false;
            _tabDragItem = null;
            if (was && TabsList.IsMouseCaptured) TabsList.ReleaseMouseCapture();
            if (was && TabsList.SelectedItem is TabVm t && !t.Value.Equals(Tab, OIC))
            {
                Tab = t.Value;
                RefreshView(keepSelection: false);
                ApplyWallpaper();
            }
        }

        // Launcher bar: drag the pills to reorder while arranging. Saved as Settings.LauncherOrder (file names).
        LauncherItem _lnDragItem;
        bool _lnDragging;
        Point _lnDragStart;

        static string LauncherKey(LauncherItem l) => Path.GetFileNameWithoutExtension(l.FilePath);

        /// <summary>Saved order (or the current one while arranging), then alphabetical for launchers not in it.</summary>
        List<LauncherItem> OrderLaunchers(IEnumerable<LauncherItem> items)
        {
            var order = _arranging ? Launchers.Select(LauncherKey).ToList() : Store.S.LauncherOrder;
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) index.TryAdd(order[i], i);
            return items.OrderBy(l => index.TryGetValue(LauncherKey(l), out var i) ? i : int.MaxValue)
                        .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        void ApplyLauncherOrder()
        {
            var ordered = OrderLaunchers(Launchers.ToList());
            if (ordered.SequenceEqual(Launchers)) return;
            Launchers.Clear();
            foreach (var l in ordered) Launchers.Add(l);
        }

        FrameworkElement LauncherContainerAt(Point p) =>
            LaunchersBar.InputHitTest(p) is DependencyObject hit ? ItemsControl.ContainerFromElement(LaunchersBar, hit) as FrameworkElement : null;

        void LaunchersBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_arranging) return;
            if (LauncherContainerAt(e.GetPosition(LaunchersBar))?.DataContext is not LauncherItem l) return;
            _lnDragItem = l;
            _lnDragStart = e.GetPosition(LaunchersBar);
            e.Handled = true; // keep the pill's button from pressing / launching
        }

        void LaunchersBar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_arranging || _lnDragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndLauncherDrag(); return; }
            var p = e.GetPosition(LaunchersBar);
            if (!_lnDragging)
            {
                if ((p - _lnDragStart).Length < 5) return;
                _lnDragging = true;
                LaunchersBar.CaptureMouse();
            }
            var c = LauncherContainerAt(p);
            if (c?.DataContext is not LauncherItem target || target == _lnDragItem) return;
            int from = Launchers.IndexOf(_lnDragItem), to = Launchers.IndexOf(target);
            if (from < 0 || to < 0) return;
            // Swap only past the target's middle so pills of different widths don't flicker (same as the tabs).
            double x = Mouse.GetPosition(c).X;
            if (from < to && x < c.ActualWidth / 2) return;
            if (from > to && x > c.ActualWidth / 2) return;
            Launchers.Move(from, to);
        }

        void LaunchersBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_lnDragItem != null) e.Handled = true;
            EndLauncherDrag();
        }

        void LaunchersBar_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_lnDragging) EndLauncherDrag();
        }

        void EndLauncherDrag()
        {
            bool was = _lnDragging;
            _lnDragging = false;
            _lnDragItem = null;
            if (was && LaunchersBar.IsMouseCaptured) LaunchersBar.ReleaseMouseCapture();
        }

        static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // ---------- card size ----------

        void ChangeTileSize(double delta)
        {
            SetTileSize(Store.S.TileMinWidth + delta);
            Flash($"Card size {Store.S.TileMinWidth:0}");
        }

        void SetTileSize(double value)
        {
            Store.S.TileMinWidth = Math.Clamp(Math.Round(value / 10) * 10, 150, 420);
            ApplyTileSize();
            SaveSoon();
            if (!_loadingSettings && CardSlider.Value != Store.S.TileMinWidth)
            {
                _loadingSettings = true;
                CardSlider.Value = Store.S.TileMinWidth;
                _loadingSettings = false;
            }
            CardSizeText.Text = $"{Store.S.TileMinWidth:0} px";
        }

        void GamesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                ChangeTileSize(e.Delta > 0 ? 20 : -20);
                e.Handled = true;
                return;
            }
            if (!Store.S.ScrollByRow) return;
            e.Handled = true;
            // Touchpads send small deltas: a row per full notch (120).
            _wheelAccum += e.Delta;
            int rows = -(int)(_wheelAccum / 120);
            if (rows == 0) return;
            _wheelAccum %= 120;
            ScrollRows(rows);
        }

        // ---------- smooth row scroll ----------
        // With Settings.ScrollByRow the wheel glides (ease-out, ~0.3 s) to the next row boundary. Rows are
        // card height + spacing apart; targets snap to that grid so the view always rests on a whole row.

        double _wheelAccum, _scrollFrom, _scrollTarget;
        DateTime _scrollStart;
        bool _scrollAnimating;
        const double ScrollMs = 300;

        ScrollViewer GamesScroll => _gamesScroll ??= FindChild<ScrollViewer>(GamesList);

        double RowHeight() =>
            GamesList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 0
                ? first.ActualHeight + Store.S.CardSpacing : 0;

        void ScrollRows(int rows)
        {
            var sv = GamesScroll;
            double row = RowHeight();
            if (sv == null || row <= 0) return;
            double basis = _scrollAnimating ? _scrollTarget : sv.VerticalOffset;
            double target = Math.Clamp(Math.Round(basis / row) * row + rows * row, 0, sv.ScrollableHeight);
            if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;
            _scrollFrom = sv.VerticalOffset;
            _scrollTarget = target;
            _scrollStart = DateTime.Now;
            if (!_scrollAnimating)
            {
                _scrollAnimating = true;
                CompositionTarget.Rendering += ScrollStep;
            }
        }

        void ScrollStep(object sender, EventArgs e)
        {
            var sv = GamesScroll;
            double t = sv == null ? 1 : Math.Min(1, (DateTime.Now - _scrollStart).TotalMilliseconds / ScrollMs);
            double eased = 1 - Math.Pow(1 - t, 3);
            sv?.ScrollToVerticalOffset(_scrollFrom + (_scrollTarget - _scrollFrom) * eased);
            if (t >= 1)
            {
                _scrollAnimating = false;
                CompositionTarget.Rendering -= ScrollStep;
            }
        }

        void SpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingSettings || !IsLoaded) return;
            Store.S.CardSpacing = (int)e.NewValue;
            SpacingText.Text = $"{Store.S.CardSpacing} px";
            ApplyTileSize();
            SaveSoon();
        }

        // ---------- random game ----------
        // The dice spins, the selection hops across the games on screen, slowing down, then lands on a random
        // game from the current filter (scrolled into view). Settings.DiceStartsGame starts it too.

        static readonly (string hex, string name)[] DiceColors =
        {
            ("#60CDFF", "Blue"), ("#FFFFFF", "White"), ("#FF5A5A", "Red"), ("#FFA94D", "Orange"),
            ("#FFD84D", "Yellow"), ("#6EE07A", "Green"), ("#B388FF", "Purple"), ("#FF77C8", "Pink")
        };
        bool _rolling;

        /// <summary>Trainer menu items, the trainer delay setting and the search hint follow Settings.TrainersEnabled.</summary>
        void ApplyTrainerUi()
        {
            bool on = Store.S.TrainersEnabled;
            Resources["TrainerVis"] = on ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Text = on ? "Enter play   ·   Ctrl+Enter with trainer" : "Enter play";
        }

        void ApplyDiceColor()
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Store.S.DiceColor));
                b.Freeze();
                Resources["DiceBrush"] = b;
            }
            catch { }
        }

        void BuildDiceSwatches()
        {
            DiceSwatches.Children.Clear();
            foreach (var (hex, name) in DiceColors)
            {
                bool on = string.Equals(hex, Store.S.DiceColor, StringComparison.OrdinalIgnoreCase);
                var ring = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(2),
                    BorderBrush = on ? (Brush)Application.Current.Resources["TextBrush"] : Brushes.Transparent,
                    Padding = new Thickness(2),
                    Margin = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                    Child = new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) }
                };
                ring.MouseLeftButtonUp += (s, e) =>
                {
                    Store.S.DiceColor = hex;
                    ApplyDiceColor();
                    BuildDiceSwatches();
                    SaveSoon();
                };
                DiceSwatches.Children.Add(ring);
            }
        }

        async void Dice_Click(object sender, RoutedEventArgs e) => await RollDice();

        async Task RollDice()
        {
            if (_rolling || _arranging) return;
            var pool = _view.Cast<GameItem>().ToList();
            if (pool.Count == 0) { Flash("No games here to pick from"); return; }
            _rolling = true;
            try
            {
                var spin = new RotateTransform();
                DiceIcon.RenderTransform = spin;
                spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 720, TimeSpan.FromMilliseconds(950)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

                var pick = pool[Random.Shared.Next(pool.Count)];
                // Hop across the cards that are on screen, so the roll is visible without the grid jumping around.
                var onScreen = pool.Where(IsOnScreen).ToList();
                if (onScreen.Count > 1)
                {
                    GameItem last = null;
                    for (int i = 0; i < 10; i++)
                    {
                        GameItem hop;
                        do hop = onScreen[Random.Shared.Next(onScreen.Count)]; while (hop == last && onScreen.Count > 1);
                        GamesList.SelectedItem = last = hop;
                        await Task.Delay(45 + i * i * 4);
                    }
                }
                GamesList.SelectedItem = pick;
                GamesList.ScrollIntoView(pick);
                if (Store.S.DiceStartsGame) Launch(pick, false);
                else Flash($"Rolled {pick.Name}  ·  Enter or the play button to start, roll again for another");
            }
            finally { _rolling = false; }
        }

        bool IsOnScreen(GameItem g)
        {
            if (GamesList.ItemContainerGenerator.ContainerFromItem(g) is not FrameworkElement c || GamesScroll == null) return false;
            var top = c.TranslatePoint(new Point(0, 0), GamesScroll).Y;
            return top + c.ActualHeight > 0 && top < GamesScroll.ViewportHeight;
        }

        void CardSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingSettings || !IsLoaded) return;
            SetTileSize(e.NewValue);
        }

        void CardReset_Click(object sender, RoutedEventArgs e) => SetTileSize(220);

        void CardShape_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            SetCardLayout(PortraitRadio.IsChecked == true ? "portrait" : "landscape");
        }

        // Filter-row button: flips between landscape and portrait cards.
        void ShapeBtn_Click(object sender, RoutedEventArgs e) =>
            SetCardLayout(Portrait ? "landscape" : "portrait");

        void SetCardLayout(string layout)
        {
            if (layout == Store.S.CardLayout) return;
            Store.S.CardLayout = layout;
            SaveSoon();
            ApplyTileSize();
            ReloadAllArt();
            UpdateShapeBtn();
            bool was = _loadingSettings;
            _loadingSettings = true; // keep the settings radios in sync without re-entering
            LandscapeRadio.IsChecked = !Portrait;
            PortraitRadio.IsChecked = Portrait;
            _loadingSettings = was;
            Flash(layout == "portrait" ? "Portrait cards" : "Landscape cards");
        }

        // The button shows the current shape: a small outline in that shape plus its name.
        void UpdateShapeBtn()
        {
            bool p = Portrait;
            ShapeGlyph.Width = p ? 9 : 14;
            ShapeGlyph.Height = p ? 13 : 9;
            ShapeText.Text = p ? "Portrait" : "Landscape";
            ShapeBtn.ToolTip = p ? "Portrait cards · click for landscape" : "Landscape cards · click for portrait";
        }

        void CardText_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            Store.S.ShowCardText = CardTextBox.IsChecked == true;
            ApplyCardLook();
            SaveSoon();
        }

        // ---------- wallpapers ----------

        string WallpaperPathFor(string tab)
        {
            if (Store.S.Wallpapers.TryGetValue(tab, out var p) && File.Exists(p)) return p;
            if (Store.S.Wallpapers.TryGetValue("*", out var d) && File.Exists(d)) return d;
            return null;
        }

        ImageSource LoadWallpaper(string path, int decodeWidth)
        {
            var key = path + "|" + decodeWidth;
            if (_wallpaperCache.TryGetValue(key, out var img)) return img;
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = decodeWidth;
                b.EndInit();
                b.Freeze();
                img = b;
            }
            catch { img = null; }
            _wallpaperCache[key] = img;
            return img;
        }

        void ApplyWallpaper()
        {
            var path = WallpaperPathFor(Tab);
            ApplyWallpaperLook();
            if (string.Equals(path, _currentWallpaper, OIC)) return;
            _currentWallpaper = path;

            var fade = new System.Windows.Media.Animation.DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };
            if (path == null)
            {
                fade.To = 0;
                WallpaperImage.BeginAnimation(OpacityProperty, fade);
                WallpaperShade.Opacity = 0;
                return;
            }
            WallpaperImage.BeginAnimation(OpacityProperty, null);
            WallpaperImage.Opacity = 0;
            WallpaperImage.Source = LoadWallpaper(path, 2560);
            fade.From = 0; fade.To = 1;
            WallpaperImage.BeginAnimation(OpacityProperty, fade);
            WallpaperShade.Opacity = Store.S.WallpaperDim;
        }

        void ApplyWallpaperLook()
        {
            bool has = WallpaperPathFor(Tab) != null;
            WallpaperShade.Opacity = has ? Store.S.WallpaperDim : 0;
            double blur = Store.S.WallpaperBlur;
            if (blur > 0)
            {
                WallpaperImage.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = blur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
                WallpaperImage.Margin = new Thickness(-blur * 2); // hide the soft blurred edges
            }
            else
            {
                WallpaperImage.Effect = null;
                WallpaperImage.Margin = new Thickness(0);
            }
        }

        void SetWallpaper(string key, string label)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Wallpaper for {label}",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.jfif|All files|*.*"
            };
            if (!ShowDlg(dlg)) return;
            ClearWallpaper(key, refresh: false);
            var safe = key == "*" ? "default" : Library.Normalize(key);
            var dest = Path.Combine(Store.WallpaperDir, $"{safe}_{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName)}");
            try
            {
                File.Copy(dlg.FileName, dest, true);
                Store.S.Wallpapers[key] = dest;
                SaveSoon();
                Flash($"Wallpaper set for {label}");
            }
            catch (Exception ex) { Flash("Couldn't copy image: " + ex.Message); }
            _currentWallpaper = "\0"; // force refresh
            ApplyWallpaper();
            if (SettingsPanel.Visibility == Visibility.Visible) BuildWallpaperRows();
        }

        void ClearWallpaper(string key, bool refresh = true)
        {
            if (Store.S.Wallpapers.TryGetValue(key, out var old))
            {
                Store.S.Wallpapers.Remove(key);
                if (!Store.S.Wallpapers.Values.Any(v => string.Equals(v, old, OIC)) &&
                    old.StartsWith(Store.WallpaperDir, OIC))
                {
                    foreach (var k in _wallpaperCache.Keys.Where(k => k.StartsWith(old, OIC)).ToList()) _wallpaperCache.Remove(k);
                    try { File.Delete(old); } catch { }
                }
                SaveSoon();
            }
            if (!refresh) return;
            ApplyWallpaper();
            if (SettingsPanel.Visibility == Visibility.Visible) BuildWallpaperRows();
        }

        void BuildWallpaperRows()
        {
            var rows = new List<WallpaperRow>();
            rows.Add(MakeRow("*", "Default", "Used by filters without their own"));
            foreach (var t in Tabs) rows.Add(MakeRow(t.Value, t.Label, null));
            WallpaperRows.ItemsSource = rows;

            WallpaperRow MakeRow(string key, string label, string note)
            {
                Store.S.Wallpapers.TryGetValue(key, out var p);
                bool exists = p != null && File.Exists(p);
                return new WallpaperRow
                {
                    Key = key,
                    Label = label,
                    Note = note ?? (exists ? Path.GetFileName(p) : (WallpaperPathFor(key) != null ? "Uses default" : "None")),
                    Thumb = exists ? LoadWallpaper(p, 160) : null,
                    IsCurrent = key.Equals(Tab, OIC)
                };
            }
        }

        void WallpaperChoose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WallpaperRow r) SetWallpaper(r.Key, r.Label);
        }

        void WallpaperClear_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WallpaperRow r) ClearWallpaper(r.Key);
        }

        void WallpaperLook_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingSettings || !IsLoaded) return;
            Store.S.WallpaperDim = Math.Round(DimSlider.Value, 2);
            Store.S.WallpaperBlur = Math.Round(BlurSlider.Value);
            DimText.Text = $"{Store.S.WallpaperDim * 100:0}%";
            BlurText.Text = $"{Store.S.WallpaperBlur:0}";
            ApplyWallpaperLook();
            SaveSoon();
        }

        void TabMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            var tab = mi.DataContext as TabVm;
            switch (mi.Tag as string)
            {
                case "set": if (tab != null) SetWallpaper(tab.Value, tab.Label); break;
                case "clear": if (tab != null) ClearWallpaper(tab.Value); break;
                case "settings": if (SettingsPanel.Visibility != Visibility.Visible) ToggleSettings(); break;
            }
        }

        // ---------- settings panel ----------

        void Settings_Click(object sender, RoutedEventArgs e) => ToggleSettings();

        void ToggleSettings()
        {
            bool open = SettingsPanel.Visibility != Visibility.Visible;
            var slide = new System.Windows.Media.Animation.DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            if (open)
            {
                LoadSettingsUi();
                SettingsPanel.Visibility = Visibility.Visible;
                slide.From = SettingsPanel.Width; slide.To = 0;
                SettingsSlide.BeginAnimation(TranslateTransform.XProperty, slide);
                SettingsBtn.Foreground = (Brush)FindResource("AccentBrush");
            }
            else
            {
                slide.From = 0; slide.To = SettingsPanel.Width;
                slide.Completed += (s, a) => SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsSlide.BeginAnimation(TranslateTransform.XProperty, slide);
                SettingsBtn.ClearValue(ForegroundProperty);
                SearchBox.Focus();
            }
        }

        void LoadSettingsUi()
        {
            _loadingSettings = true;
            var s = Store.S;
            CardSlider.Value = s.TileMinWidth;
            CardSizeText.Text = $"{s.TileMinWidth:0} px";
            LandscapeRadio.IsChecked = !Portrait;
            PortraitRadio.IsChecked = Portrait;
            CardTextBox.IsChecked = s.ShowCardText;
            SpacingSlider.Value = s.CardSpacing;
            SpacingText.Text = $"{s.CardSpacing} px";
            ScrollByRowBox.IsChecked = s.ScrollByRow;
            DiceStartsBox.IsChecked = s.DiceStartsGame;
            BuildDiceSwatches();
            DimSlider.Value = s.WallpaperDim;
            BlurSlider.Value = s.WallpaperBlur;
            DimText.Text = $"{s.WallpaperDim * 100:0}%";
            BlurText.Text = $"{s.WallpaperBlur:0}";
            FolderBox.Text = s.Folder;
            UpdateFolderStatus();
            HotkeyBox.Text = s.Hotkey;
            HotkeyStatus.Text = _hotkeyOk ? "Click the box, then press a combination" : "That combination is taken by another app";
            DelaySlider.Value = s.TrainerDelaySeconds;
            DelayText.Text = $"{s.TrainerDelaySeconds}s";
            HideOnLaunchBox.IsChecked = s.HideOnLaunch;
            HideOnFocusLossBox.IsChecked = s.HideOnFocusLoss;
            CloseToTrayBox.IsChecked = s.CloseToTray;
            FetchCoversBox.IsChecked = s.FetchCovers;
            TipsBox.IsChecked = s.ShowTips;
            TrainersBox.IsChecked = s.TrainersEnabled;
            HeroBox.IsChecked = s.HeroBackground;
            AccentBox.IsChecked = s.AccentFromGame;
            CompactHeaderBox.IsChecked = s.CompactHeader;
            SpotlightBox.IsChecked = s.Spotlight;
            SpotlightSlider.Value = s.SpotlightDim;
            SpotlightText.Text = $"{s.SpotlightDim}%";
            SpotlightSlider.IsEnabled = s.Spotlight;
            StartupBox.IsChecked = StartupReg.IsEnabled();
            CheckUpdatesBox.IsChecked = s.CheckForUpdates;
            AutoInstallBox.IsChecked = s.AutoInstallUpdates;
            UpdateUpdateUi();
            BuildWallpaperRows();
            _loadingSettings = false;
        }

        void FolderBrowse_Click(object sender, RoutedEventArgs e)
        {
            var current = Store.S.Folder;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose your games shortcuts folder",
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            _dialogOpen = true;
            bool ok;
            try { ok = dlg.ShowDialog(this) == true; } finally { _dialogOpen = false; }
            if (!ok || string.IsNullOrWhiteSpace(dlg.FolderName)) return;
            // On first run there is no folder yet (empty string), and Path.GetFullPath("") throws.
            if (!string.IsNullOrWhiteSpace(current)
                && string.Equals(Path.GetFullPath(dlg.FolderName).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), OIC)) return;
            ChangeFolder(dlg.FolderName);
        }

        void ChangeFolder(string folder)
        {
            Store.S.Folder = folder;
            Store.Save();
            FolderBox.Text = folder;
            // Saved data (tags, trainers, covers, play history) is keyed by path relative to the folder,
            // so it carries over when the new folder has the same shortcut names.
            foreach (var g in Games.ToList()) Games.Remove(g);
            StartWatching();
            _ = Rescan().ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateFolderStatus();
                Flash($"Now showing {folder}");
            })));
        }

        void UpdateFolderStatus()
        {
            var f = Store.S.Folder;
            if (string.IsNullOrWhiteSpace(f) || !Directory.Exists(f))
                FolderStatus.Text = "This folder doesn't exist — choose another one.";
            else
            {
                int games = Games.Count(IsGame), other = Games.Count - games;
                FolderStatus.Text = $"{games} games" + (other > 0 ? $", {other} tools" : "") + $", {Launchers.Count} launchers, {_trainers.Count} trainers found";
            }
        }

        void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.Tab or Key.Escape) return;

            var parts = new List<string>();
            var m = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
            if ((m & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((m & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((m & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if (parts.Count == 0) { HotkeyStatus.Text = "Use at least one of Ctrl, Alt, Shift or Win"; return; }
            var name = key >= Key.D0 && key <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();
            parts.Add(name);

            var combo = string.Join("+", parts);
            var previous = Store.S.Hotkey;
            Store.S.Hotkey = combo;
            RegisterHotkey();
            if (_hotkeyOk)
            {
                HotkeyBox.Text = combo;
                HotkeyStatus.Text = "Saved";
                SaveSoon();
            }
            else
            {
                HotkeyStatus.Text = $"{combo} is taken by another app — try another";
                Store.S.Hotkey = previous;
                RegisterHotkey();
            }
        }

        void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingSettings || !IsLoaded) return;
            Store.S.TrainerDelaySeconds = (int)e.NewValue;
            DelayText.Text = $"{Store.S.TrainerDelaySeconds}s";
            SaveSoon();
        }

        void Option_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            var s = Store.S;
            s.HideOnLaunch = HideOnLaunchBox.IsChecked == true;
            s.HideOnFocusLoss = HideOnFocusLossBox.IsChecked == true;
            s.CloseToTray = CloseToTrayBox.IsChecked == true;
            bool fetchBefore = s.FetchCovers;
            s.FetchCovers = FetchCoversBox.IsChecked == true;
            s.ScrollByRow = ScrollByRowBox.IsChecked == true;
            s.DiceStartsGame = DiceStartsBox.IsChecked == true;
            s.ShowTips = TipsBox.IsChecked == true;
            if (s.TrainersEnabled != (TrainersBox.IsChecked == true))
            {
                s.TrainersEnabled = TrainersBox.IsChecked == true;
                AssignTrainers();  // clears or re-detects every game's trainer
                RebuildTabs();     // adds / removes "With trainer" (falls back to the first tab if it was open)
                RefreshView(keepSelection: true);
            }
            if (sender == StartupBox)
            {
                try { StartupReg.Set(StartupBox.IsChecked == true); }
                catch (Exception ex) { Flash("Couldn't change startup setting: " + ex.Message); }
            }
            if (s.FetchCovers && !fetchBefore) foreach (var g in Games.Where(g => g.Cover == null)) _ = LoadArt(g);
            SaveSoon();
        }

        void OpenDataFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Store.Dir);

        // ---------- look: hero background, game accent, compact header ----------
        // Each has its own switch (Settings → Look). The "game in focus" is the card under the pointer, else the
        // selected one; changes are debounced so sweeping the mouse across the grid doesn't flicker.

        static readonly Color DefaultAccent = Color.FromRgb(0x60, 0xCD, 0xFF);
        Color _accentTarget = DefaultAccent;
        GameItem _hoverGame, _heroGame;
        ImageSource _heroShown;
        DispatcherTimer _focusTimer;
        int _heroVer;
        bool _heroFrontA;
        readonly Dictionary<string, ImageSource> _heroCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        void InitLook()
        {
            _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _focusTimer.Tick += (s, e) => { _focusTimer.Stop(); UpdateFocusLook(); };
            GamesList.SelectionChanged += (s, e) => FocusSoon();
            // Leaving the grid (not just a card: the gaps between cards keep the last one) goes back to the selection.
            GamesList.MouseLeave += (s, e) => { _hoverGame = null; FocusSoon(); Spotlight(null); };
            HeroLayer.SizeChanged += (s, e) => UpdateBannerLayout();
            LaunchersBar.SizeChanged += (s, e) => UpdateBannerLayout();
            TabsList.SizeChanged += (s, e) => UpdateBannerLayout();
            GamesList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => UpdateTopFade()));
            Loaded += (s, e) => UpdateBannerLayout();
            ApplyHeaderLook();
        }

        GameItem FocusGame => _hoverGame ?? GamesList.SelectedItem as GameItem;

        /// <summary>After the background art of g changed: show it now if g is the game in focus.</summary>
        void RefreshHero(GameItem g)
        {
            if (g != FocusGame && g != _heroGame) return;
            _heroGame = null;
            UpdateFocusLook();
        }

        const double TopFadeHeight = 44;
        LinearGradientBrush _topFade;

        /// <summary>Once the grid is scrolled, cards passing under the filter row fade out instead of being cut off
        /// sharply. The fade grows with the first pixels of scrolling, so the top row looks normal at rest.</summary>
        void UpdateTopFade()
        {
            double offset = GamesScroll?.VerticalOffset ?? 0;
            if (offset <= 0.5) { GamesList.OpacityMask = null; return; }
            if (_topFade == null)
            {
                _topFade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0) };
                _topFade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                _topFade.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 0.4));
                _topFade.GradientStops.Add(new GradientStop(Colors.Black, 1));
            }
            _topFade.EndPoint = new Point(0, Math.Min(offset, TopFadeHeight));
            GamesList.OpacityMask = _topFade;
        }

        void FocusSoon()
        {
            _focusTimer.Stop();
            _focusTimer.Start();
        }

        async void UpdateFocusLook()
        {
            var s = Store.S;
            var g = FocusGame;
            if (g != null && !_view.Contains(g)) g = GamesList.SelectedItem as GameItem;
            ApplyAccent(s.AccentFromGame ? g : null);
            UpdateHeroInfo(s.HeroBackground ? g : null);

            if (!s.HeroBackground) g = null;
            if (g == _heroGame && (g == null || _heroShown != null)) return;
            _heroGame = g;
            int ver = ++_heroVer;
            var (banner, ambient) = g == null ? (null, null) : await HeroImagesFor(g);
            if (ver != _heroVer) return;
            ShowHero(banner, ambient);
        }

        /// <summary>Steam's hero art is about 3.1:1. The banner shows all of it across the window's width; the space
        /// above the filters (HeroInfo) grows so roughly the top three quarters of it stay clear of the cards.</summary>
        void UpdateBannerLayout()
        {
            if (!IsLoaded) return;
            double w = HeroLayer.ActualWidth, h = HeroLayer.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double banner = Math.Min(w / 3.1, h * 0.55);
            HeroBanA.Height = HeroBanB.Height = banner;
            double top = HeroInfo.Visibility == Visibility.Visible
                ? HeroInfo.TranslatePoint(new Point(0, 0), HeroLayer).Y : 0;
            HeroInfo.Height = Math.Clamp(banner * 0.74 - top, 56, 260);
            // Scrim from about the middle of the space above the filters down to the bottom of the filter row.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (HeroInfo.Visibility != Visibility.Visible) { HeroScrim.Height = 0; return; }
                double infoTop = HeroInfo.TranslatePoint(new Point(0, 0), HeroLayer).Y;
                double tabsBottom = TabsList.TranslatePoint(new Point(0, TabsList.ActualHeight), HeroLayer).Y + 10;
                double scrimTop = infoTop + HeroInfo.ActualHeight * 0.35;
                HeroScrim.Margin = new Thickness(0, scrimTop, 0, 0);
                // Dark down to the filter row, then a soft tail behind the first cards (no hard edge).
                HeroScrim.Height = Math.Max(0, (tabsBottom - scrimTop) / 0.68);
            }), DispatcherPriority.Loaded);
        }

        void UpdateHeroInfo(GameItem g)
        {
            var vis = Store.S.HeroBackground ? Visibility.Visible : Visibility.Collapsed;
            if (HeroInfo.Visibility != vis)
            {
                HeroInfo.Visibility = vis;
                Dispatcher.BeginInvoke(new Action(UpdateBannerLayout), DispatcherPriority.Loaded);
            }
            HeroName.Text = g?.Name ?? "";
            if (g == null) { HeroSub.Text = ""; return; }
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(g.SubText)) parts.Add(g.SubText);
            if (g.HasTrainer) parts.Add("Trainer ready");
            HeroSub.Text = string.Join("  ·  ", parts);
        }

        /// <summary>A banner image must be at least this many pixels wide; smaller art stretched across the window
        /// looks smeared, so it's left out and only the dark ambient wash shows.</summary>
        const int MinBannerPixels = 1000;

        /// <summary>Art for the game in focus. Banner: the first sharp, wide picture of: Steam's library hero, the
        /// Microsoft Store / GOG hero art (games Steam doesn't have), the custom landscape cover, the store's landscape
        /// cover, the Steam header — null when none is sharp enough. Ambient: the banner, else the cover's tiny
        /// blurred copy. Both null when the game has no art at all.</summary>
        async Task<(ImageSource banner, ImageSource ambient)> HeroImagesFor(GameItem g)
        {
            var d = Store.Find(g.Key);
            // Background art the user chose wins, whatever its size (their call).
            if (!string.IsNullOrEmpty(d?.CustomHero) && File.Exists(d.CustomHero))
            {
                if (!_heroCache.TryGetValue(d.CustomHero, out var mine))
                    _heroCache[d.CustomHero] = mine = await Task.Run(() => Covers.LoadImageAtMost(d.CustomHero, 2560));
                if (mine != null) return (mine, mine);
            }
            if (d?.NoCover == true) return (null, null);
            int? id = g.SteamAppId ?? d?.SteamAppId;
            bool fetch = Store.S.FetchCovers;

            async Task<string> Fetch(Func<Task<string>> get)
            {
                await NetGate.WaitAsync();
                try { return await get(); }
                finally { NetGate.Release(); }
            }
            static string Existing(string p) => !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;

            var candidates = new List<Func<Task<string>>>();
            if (id != null)
                candidates.Add(async () => Existing(Covers.CachedHeroPath(id.Value))
                                           ?? (fetch ? await Fetch(() => Covers.EnsureSteamHero(id.Value)) : null));
            else
                candidates.Add(async () => Existing(Covers.StoreHeroPath(g.Key))
                                           ?? (fetch ? await Fetch(() => Covers.EnsureStoreHero(g.Key, g.Name)) : null));
            candidates.Add(() => Task.FromResult(Existing(d?.CustomCover)));
            candidates.Add(() => Task.FromResult(Existing(Covers.StoreCachedPath(g.Key, false))));
            if (id != null) candidates.Add(() => Task.FromResult(Existing(Covers.CachedPath(id.Value))));

            foreach (var get in candidates)
            {
                var path = await get();
                if (path == null) continue;
                if (!_heroCache.TryGetValue(path, out var img))
                {
                    img = await Task.Run(() => Covers.LoadImageAtMost(path, 1920));
                    if (_heroCache.Count > 24) _heroCache.Clear();
                    _heroCache[path] = img;
                }
                if (img is System.Windows.Media.Imaging.BitmapSource b && b.PixelWidth >= MinBannerPixels) return (img, img);
            }
            return (null, g.CoverFill);
        }

        /// <summary>Cross-fades to new art. banner null = no sharp wide picture: only the dark ambient wash shows.</summary>
        void ShowHero(ImageSource banner, ImageSource ambient)
        {
            _heroShown = ambient;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (ambient == null)
            {
                Animate(HeroLayer, OpacityProperty, 0, 300, ease);
                return;
            }
            // Bright art (a yellow Counter-Strike hero) would wash out the text over it: the brighter, the more shade.
            Animate(HeroSoftShade, OpacityProperty, Math.Clamp((Brightness(banner ?? ambient) - 0.2) * 1.4, 0, 0.55), 380, ease);
            var (front, back) = _heroFrontA ? (HeroA, HeroB) : (HeroB, HeroA);
            bool layerWasHidden = HeroLayer.Opacity < 0.01;
            if (back == HeroA) { HeroAmbA.Source = ambient; HeroBanA.Source = banner; }
            else { HeroAmbB.Source = ambient; HeroBanB.Source = banner; }
            if (layerWasHidden)
            {
                front.BeginAnimation(OpacityProperty, null); front.Opacity = 0;
                back.BeginAnimation(OpacityProperty, null); back.Opacity = 1;
            }
            else
            {
                Animate(back, OpacityProperty, 1, 380, ease);
                Animate(front, OpacityProperty, 0, 380, ease);
            }
            _heroFrontA = !_heroFrontA;
            Animate(HeroLayer, OpacityProperty, 1, 300, ease);
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, object> Brightnesses = new();

        /// <summary>Average brightness (0–1) of an image, from a 32 px wide copy. Cached per image.</summary>
        static double Brightness(ImageSource img)
        {
            if (img is not System.Windows.Media.Imaging.BitmapSource src || src.PixelWidth == 0) return 0;
            if (Brightnesses.TryGetValue(img, out var cached)) return (double)cached;
            double result = 0;
            try
            {
                double k = Math.Min(1, 32.0 / src.PixelWidth);
                var small = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    new System.Windows.Media.Imaging.TransformedBitmap(src, new ScaleTransform(k, k)), PixelFormats.Bgra32, null, 0);
                int w = small.PixelWidth, h = small.PixelHeight;
                var px = new byte[w * h * 4];
                small.CopyPixels(px, w * 4, 0);
                double sum = 0;
                for (int i = 0; i < px.Length; i += 4) sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                result = sum / (w * h) / 255.0;
            }
            catch { }
            Brightnesses.AddOrUpdate(img, result);
            return result;
        }

        /// <summary>Glides the window's accent to a bright, readable version of the game's cover colour (or back to the
        /// default blue when g is null).</summary>
        void ApplyAccent(GameItem g)
        {
            var target = g == null ? DefaultAccent : ReadableAccent(GlowColor(g));
            if (target == _accentTarget) return;
            _accentTarget = target;
            _accentFrom = _accentNow;
            _accentStart = DateTime.UtcNow;
            if (!_accentAnimating) { _accentAnimating = true; CompositionTarget.Rendering += AccentTick; }
        }

        // Styles and templates freeze the brushes they use, so the colour can't be animated in place: each frame of
        // the ~300 ms glide puts a new (frozen) brush in the window's AccentBrush resource instead.
        Color _accentFrom = DefaultAccent, _accentNow = DefaultAccent;
        DateTime _accentStart;
        bool _accentAnimating;

        void AccentTick(object sender, EventArgs e)
        {
            double t = Math.Min(1, (DateTime.UtcNow - _accentStart).TotalMilliseconds / 300);
            double k = 1 - Math.Pow(1 - t, 3);
            byte L(byte a, byte b) => (byte)Math.Round(a + (b - a) * k);
            _accentNow = Color.FromRgb(L(_accentFrom.R, _accentTarget.R), L(_accentFrom.G, _accentTarget.G), L(_accentFrom.B, _accentTarget.B));
            var brush = new SolidColorBrush(_accentNow);
            brush.Freeze();
            Resources["AccentBrush"] = brush;
            if (t >= 1) { CompositionTarget.Rendering -= AccentTick; _accentAnimating = false; }
        }

        /// <summary>Same hue, but saturated enough to read as a colour and bright enough for text on the dark theme.</summary>
        static Color ReadableAccent(Color c)
        {
            double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(gg, b)), min = Math.Min(r, Math.Min(gg, b)), d = max - min;
            if (d < 0.08) return DefaultAccent; // grey cover: keep the default
            double h = max == r ? 60 * (((gg - b) / d) % 6) : max == gg ? 60 * ((b - r) / d + 2) : 60 * ((r - gg) / d + 4);
            if (h < 0) h += 360;
            double s = Math.Clamp(d / max, 0.4, 0.72);
            return Covers.FromHsv(h, s, 1.0);
        }

        void ApplyHeaderLook()
        {
            bool compact = Store.S.CompactHeader;
            var full = compact ? Visibility.Collapsed : Visibility.Visible;
            RefreshBtn.Visibility = full;
            FolderBtn.Visibility = full;
            SettingsBtn.Visibility = full;
            MoreBtn.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            AppTitle.Visibility = !compact && ActualWidth >= 960 ? Visibility.Visible : Visibility.Collapsed;
        }

        void More_Click(object sender, RoutedEventArgs e)
        {
            var menu = MoreBtn.ContextMenu;
            menu.PlacementTarget = MoreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();

        void Look_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            var s = Store.S;
            s.HeroBackground = HeroBox.IsChecked == true;
            s.AccentFromGame = AccentBox.IsChecked == true;
            s.CompactHeader = CompactHeaderBox.IsChecked == true;
            s.Spotlight = SpotlightBox.IsChecked == true;
            if (!s.Spotlight) Spotlight(null);
            SpotlightSlider.IsEnabled = s.Spotlight;
            ApplyHeaderLook();
            _heroGame = null; _heroShown = null; // re-evaluate now
            UpdateFocusLook();
            SaveSoon();
        }

        // ---------- updates ----------
        // Updater talks to GitHub; this part decides when to ask and shows the result: the accent "Update" button in
        // the title bar, the ⋯ menu item and Settings → Updates.

        UpdateInfo _update;          // a newer release, once found
        bool _checking, _installing;
        string _updateError;
        DispatcherTimer _updateTimer;

        void InitUpdates()
        {
            // Look shortly after every start, then every hour whether a day has passed (the launcher lives in the tray for days).
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            bool atStart = true;
            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(1);
                var last = Store.S.LastUpdateCheck;
                bool due = atStart || last == null || DateTime.Now - last.Value > TimeSpan.FromHours(20);
                atStart = false;
                if (Store.S.CheckForUpdates && due)
                    _ = CheckForUpdate(manual: false);
                else if (_update != null) MaybeAutoInstall();
            };
            _updateTimer.Start();
            IsVisibleChanged += (s, e) => { if (!IsVisible) MaybeAutoInstall(); };
            UpdateUpdateUi();
        }

        public void AnnounceUpdated() =>
            Dispatcher.BeginInvoke(new Action(() => Flash($"Updated to version {Updater.CurrentText}")), DispatcherPriority.ApplicationIdle);

        async Task CheckForUpdate(bool manual)
        {
            if (_checking || _installing) return;
            _checking = true; _updateError = null;
            UpdateUpdateUi();
            try
            {
                _update = await Updater.CheckAsync();
                Store.S.LastUpdateCheck = DateTime.Now;
                SaveSoon();
                if (manual) Flash(_update != null ? $"Version {_update.Version.ToString(3)} is available" : "You have the latest version");
            }
            catch (Exception ex)
            {
                _updateError = "Couldn't check for updates";
                if (manual) Flash(_updateError + ": " + ex.Message);
            }
            finally
            {
                _checking = false;
                UpdateUpdateUi();
            }
            if (_update != null) MaybeAutoInstall();
        }

        void MaybeAutoInstall()
        {
            if (_update != null && Store.S.AutoInstallUpdates && !IsVisible && !_installing && Updater.CanSelfUpdate)
                _ = InstallUpdate(hidden: true);
        }

        async Task InstallUpdate(bool hidden)
        {
            if (_update == null || _installing) return;
            if (!Updater.CanSelfUpdate || _update.DownloadUrl == null)
            {
                // A development build (or a release without the exe): let the user get it from the page.
                try { Process.Start(new ProcessStartInfo(_update.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true }); } catch { }
                return;
            }
            _installing = true;
            UpdateUpdateUi();
            var version = _update.Version.ToString(3);
            int lastPct = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = p < 0 ? -1 : (int)(p * 100);
                if (pct == lastPct) return;
                lastPct = pct;
                Flash(pct < 0 ? $"Downloading version {version}…" : $"Downloading version {version}…  {pct}%");
                UpdateBtnText.Text = pct < 0 ? "Downloading…" : $"Downloading {pct}%";
            });
            try
            {
                var exe = await Updater.DownloadAndInstallAsync(_update, progress);
                Flash($"Restarting with version {version}…");
                ((App)Application.Current).RestartInto(exe, hidden || !IsVisible);
            }
            catch (Exception ex)
            {
                _installing = false;
                _updateError = "The update didn't install";
                Flash($"{_updateError}: {ex.Message}");
                UpdateUpdateUi();
            }
        }

        async void UpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            if (_update != null) await InstallUpdate(hidden: false);
            else await CheckForUpdate(manual: true);
        }

        void UpdateOption_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            Store.S.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
            Store.S.AutoInstallUpdates = AutoInstallBox.IsChecked == true;
            SaveSoon();
            UpdateUpdateUi();
            if (Store.S.CheckForUpdates && _update == null && Store.S.LastUpdateCheck == null) _ = CheckForUpdate(manual: false);
        }

        void UpdateUpdateUi()
        {
            if (VersionText == null) return;
            string newer = _update?.Version.ToString(3);
            UpdateBtn.Visibility = _update != null ? Visibility.Visible : Visibility.Collapsed;
            if (!_installing) UpdateBtnText.Text = $"Update to {newer}";
            UpdateBtn.ToolTip = Updater.CanSelfUpdate
                ? $"Download version {newer} and restart. Settings and covers are kept."
                : $"Open the download page for version {newer}";
            UpdateMenuItem.Header = _update != null ? $"Update to {newer}" : "Check for updates";

            VersionText.Text = $"Version {Updater.CurrentText}";
            UpdateStateText.Text =
                _installing ? $"Downloading version {newer}…"
                : _checking ? "Checking…"
                : _update != null ? $"Version {newer} is available."
                : _updateError != null ? _updateError + "."
                : Store.S.LastUpdateCheck is DateTime t ? $"Up to date · checked {(t.Date == DateTime.Today ? "today" : t.ToString("d MMM"))} at {t:HH:mm}"
                : "Not checked yet";
            UpdateNowBtn.Content = _update != null ? (Updater.CanSelfUpdate ? "Update now" : "Download") : "Check now";
            UpdateNowBtn.IsEnabled = !_checking && !_installing;
            AutoInstallBox.IsEnabled = CheckUpdatesBox.IsChecked == true;
        }

        // ---------- title bar ----------

        void Refresh_Click(object sender, RoutedEventArgs e) { _ = Rescan(); Flash("Rescanned"); }
        void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Store.S.Folder);
        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
