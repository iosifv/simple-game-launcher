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
            Width = Math.Max(MinWidth, Store.S.Width);
            Height = Math.Max(MinHeight, Store.S.Height);
            Tab = Store.S.DefaultTab;

            ApplyCardLook();

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
                MaxBtn.Content = max ? "" : "";
            };
            SizeChanged += (s, e) =>
            {
                if (IsLoaded && WindowState == WindowState.Normal)
                {
                    Store.S.Width = ActualWidth;
                    Store.S.Height = ActualHeight;
                    SaveSoon();
                }
            };
            Deactivated += (s, e) =>
            {
                if (Store.S.HideOnFocusLoss && !_dialogOpen && OwnedWindows.Count == 0 && !AnyContextMenuOpen()) Hide();
            };
        }

        // ---------- lifecycle ----------

        public void Initialize()
        {
            new WindowInteropHelper(this).EnsureHandle();
            StartWatching();
            _ = Rescan();
            // First run (or the folder is gone): open settings so a folder can be chosen.
            if (string.IsNullOrWhiteSpace(Store.S.Folder) || !Directory.Exists(Store.S.Folder))
                Dispatcher.BeginInvoke(new Action(() => { if (SettingsPanel.Visibility != Visibility.Visible) ToggleSettings(); }),
                                       DispatcherPriority.ApplicationIdle);
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
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

        public void ShowLauncher()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
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
            _orderIndex = null;
            RegisterHotkey();
            StartWatching();
            foreach (var g in Games) g.LastWrite = DateTime.MinValue; // forces icon/cover reload
            _ = Rescan();
            Flash("Settings reloaded");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
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
                g.Name = e.Name;
                g.Folder = e.Folder;
                g.Tags = d?.Tags?.ToList() ?? new List<string>();
                g.LastPlayed = d?.LastPlayed;
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
            var newLaunchers = r.Launchers.OrderBy(l => l.SortKey, StringComparer.CurrentCultureIgnoreCase).Select(l =>
            {
                if (oldLaunchers.TryGetValue(l.FilePath, out var li) && li.LastWrite == l.LastWrite) return li;
                var item = new LauncherItem { Name = l.Name, FilePath = l.FilePath, LastWrite = l.LastWrite };
                _ = LoadLauncherIcon(item);
                return item;
            }).ToList();
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

            var d = Store.Find(g.Key);
            bool portrait = Portrait;
            if (d?.NoCover == true) { SetCover(g, null, null); return; }

            // Custom covers win; the one matching the card shape first (a hand-picked cover beats Steam's other art).
            var custom = (portrait ? new[] { d?.CustomCoverPortrait, d?.CustomCover } : new[] { d?.CustomCover, d?.CustomCoverPortrait })
                .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
            if (custom != null) { await ShowCover(g, ver, custom); return; }

            int? id = g.SteamAppId ?? d?.SteamAppId;
            if (id != null)
            {
                var cached = portrait ? Covers.CachedPortraitPath(id.Value) : Covers.CachedPath(id.Value);
                if (File.Exists(cached)) { await ShowCover(g, ver, cached); return; }
            }
            if (!Store.S.FetchCovers)
            {
                // Offline: a portrait card can still show the landscape header if we have it.
                if (id != null && portrait && File.Exists(Covers.CachedPath(id.Value))) await ShowCover(g, ver, Covers.CachedPath(id.Value));
                else SetCover(g, null, null);
                return;
            }

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
                {
                    var path = portrait
                        ? await Covers.EnsureSteamPortrait(id.Value) ?? await Covers.EnsureSteamCover(id.Value)
                        : await Covers.EnsureSteamCover(id.Value);
                    if (path != null) await ShowCover(g, ver, path);
                }
            }
            finally { NetGate.Release(); }
        }

        async Task ShowCover(GameItem g, int ver, string path)
        {
            var (img, fill) = await Task.Run(() => (Covers.LoadImage(path), Covers.LoadImage(path, 24)));
            if (ver == g.ArtVersion) SetCover(g, img, fill);
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
            _view.Refresh();
            if (keepSelection && sel != null && _view.Contains(sel)) GamesList.SelectedItem = sel;
            else SelectFirst();
            UpdateStatus();
        }

        List<TabVm> DefaultTabs()
        {
            var list = new List<TabVm> { new TabVm("Games", "games"), new TabVm("All", "all"), new TabVm("Recent", "recent") };
            if (Games.Any(g => g.HasTrainer)) list.Add(new TabVm("With trainer", "trainer"));
            foreach (var f in Games.Select(g => g.Folder).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f))
                list.Add(new TabVm(f, "folder:" + f));
            foreach (var t in Games.SelectMany(g => g.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t))
                list.Add(new TabVm("#" + t, "tag:" + t));
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
                int games = Games.Count(IsGame), other = Games.Count - games;
                StatusText.Text = $"{games} games" + (other > 0 ? $"  ·  {other} tools" : "")
                                  + $"  ·  {Games.Count(g => g.HasTrainer)} with trainers  ·  watching {Store.S.Folder}";
            }
            HotkeyText.Text = _hotkeyOk ? $"{Store.S.Hotkey}  show / hide" : $"Hotkey {Store.S.Hotkey} is taken — change it in settings";
            CountText.Text = _view.Count == Games.Count ? "" : $"{_view.Count} of {Games.Count}";

            EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = !_rootExists ? "Shortcuts folder not found"
                : QNorm.Length > 0 ? $"No games match \"{Query}\""
                : Tab == "recent" ? "Games you launch from here will show up in Recent"
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
            const double margin = 12, scrollbar = 18;
            double avail = GamesList.ActualWidth - scrollbar;
            if (avail <= 0) return;
            int cols = Math.Max(1, (int)(avail / (minTile + margin)));
            double tile = Math.Floor(avail / cols - margin);
            Resources["TileWidth"] = tile;
            Resources["ArtHeight"] = Math.Round(Portrait ? tile * 1.5 : tile * 215.0 / 460.0);
        }

        /// <summary>Names under the cards on/off: swaps the template resources.</summary>
        void ApplyCardLook()
        {
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
                MessageBox.Show(this, $"Couldn't start {g.Name}.\n\n{error}", "Game Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? ""
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
                        Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*",
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

                case "setcover":
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = $"Choose a cover for {g.Name}",
                        Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*"
                    };
                    if (ShowDlg(dlg))
                    {
                        var d = Store.Game(g.Key);
                        // A tall image becomes the portrait cover, a wide one the landscape cover.
                        var probe = Covers.LoadImage(dlg.FileName, 64);
                        bool tall = probe != null && probe.Height > probe.Width;
                        TryDelete(tall ? d.CustomCoverPortrait : d.CustomCover);
                        var dest = Path.Combine(Store.CoverDir, $"custom_{Library.Normalize(g.Key)}{(tall ? "_portrait" : "")}_{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName)}");
                        try
                        {
                            File.Copy(dlg.FileName, dest, true);
                            if (tall) d.CustomCoverPortrait = dest; else d.CustomCover = dest;
                            d.NoCover = false;
                            Flash(tall ? "Portrait cover set" : "Landscape cover set");
                        }
                        catch (Exception ex) { Flash("Couldn't copy image: " + ex.Message); }
                        SaveSoon();
                        _ = LoadArt(g);
                    }
                    break;
                }
                case "steamcover":
                {
                    var d = Store.Game(g.Key);
                    TryDelete(d.CustomCover);
                    TryDelete(d.CustomCoverPortrait);
                    d.CustomCover = null;
                    d.CustomCoverPortrait = null;
                    d.NoCover = false;
                    if (g.SteamAppId == null && d.SteamAppId == null) d.SteamLookupDone = false;
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
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{g.FilePath}\"") { UseShellExecute = true }); } catch { }
                    break;
            }
        }

        void AfterTrainerChange()
        {
            SaveSoon();
            AssignTrainers();
            RebuildTabs();
            RefreshView(keepSelection: true);
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
            Flash("Games A–Z, filters in default order  ·  click Done to keep it");
        }

        void EnterArrange()
        {
            if (_arranging) return;
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
            Placeholder.Text = "Arrange mode — drag tiles or filters to reorder, then click Done";
            SearchHint.Visibility = Visibility.Collapsed;
            RefreshView(keepSelection: true);
            GamesList.Focus();
        }

        void ExitArrange(bool save)
        {
            if (!_arranging) return;
            if (_dragging) EndDrag(true);
            EndTabDrag();
            if (save)
            {
                Store.S.CustomOrder = _alphaReset ? new List<string>() : Games.Select(g => g.Key).ToList();
                _orderIndex = null;
                var tabOrder = Tabs.Select(t => t.Value).ToList();
                Store.S.TabOrder = tabOrder.SequenceEqual(DefaultTabs().Select(t => t.Value)) ? new List<string>() : tabOrder;
                SaveSoon();
            }
            _arranging = false;
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
            if (_arranging) e.Handled = true;
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
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ChangeTileSize(e.Delta > 0 ? 20 : -20);
            e.Handled = true;
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
            var layout = PortraitRadio.IsChecked == true ? "portrait" : "landscape";
            if (layout == Store.S.CardLayout) return;
            Store.S.CardLayout = layout;
            SaveSoon();
            ApplyTileSize();
            ReloadAllArt();
            Flash(layout == "portrait" ? "Portrait cards" : "Landscape cards");
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
            StartupBox.IsChecked = StartupReg.IsEnabled();
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
            if (sender == StartupBox)
            {
                try { StartupReg.Set(StartupBox.IsChecked == true); }
                catch (Exception ex) { Flash("Couldn't change startup setting: " + ex.Message); }
            }
            if (s.FetchCovers && !fetchBefore) foreach (var g in Games.Where(g => g.Cover == null)) _ = LoadArt(g);
            SaveSoon();
        }

        void OpenDataFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Store.Dir);

        // ---------- title bar ----------

        void Refresh_Click(object sender, RoutedEventArgs e) { _ = Rescan(); Flash("Rescanned"); }
        void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Store.S.Folder);
        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
