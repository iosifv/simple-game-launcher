using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameLauncher
{
    /// <summary>
    /// Searches Steam for art and lets the user pick a landscape and a portrait cover separately.
    /// Returns the chosen images' bytes (either may be null = keep the current one), or null on cancel.
    /// </summary>
    class CoverPicker
    {
        public byte[] Landscape, Portrait;

        const double LandW = 232, LandH = 124, PortW = 136, PortH = 204;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static readonly Brush TileBg = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        static readonly Brush HoverBorder = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        readonly Window _w;
        readonly TextBox _box = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        readonly WrapPanel _land = new WrapPanel(), _port = new WrapPanel();
        readonly TextBlock _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Button _save = new Button { Content = "Save", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        Border _selLand, _selPort;
        string _selLandName, _selPortName;
        int _searchVer;

        public static CoverPicker Show(Window owner, string gameName)
        {
            var p = new CoverPicker(owner, gameName);
            return p._w.ShowDialog() == true ? p : null;
        }

        CoverPicker(Window owner, string gameName)
        {
            _w = new Window
            {
                Title = "Find cover",
                Owner = owner,
                Width = 1040,
                Height = 760,
                MinWidth = 640,
                MinHeight = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)),
                Foreground = Res("TextBrush")
            };
            _status.Foreground = Res("SecondaryBrush");
            _summary.Foreground = Res("SecondaryBrush");
            _box.Text = gameName;

            var search = new Button { Content = "Search", MinWidth = 90, IsDefault = true };
            search.Click += (s, e) => _ = Search();
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            _save.Click += (s, e) => _w.DialogResult = true;

            var searchRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(search, Dock.Right);
            searchRow.Children.Add(search);
            searchRow.Children.Add(_box);

            var header = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
            header.Children.Add(new TextBlock { Text = $"Find a cover for {gameName}", FontSize = 18, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "Pick one landscape and one portrait image, then save. You can pick just one of them. Images come from Steam, the Microsoft Store and GOG.",
                Foreground = Res("SecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            header.Children.Add(searchRow);
            header.Children.Add(_status);

            var results = new StackPanel { Margin = new Thickness(24, 4, 12, 12) };
            results.Children.Add(SectionTitle("Landscape"));
            results.Children.Add(_land);
            results.Children.Add(SectionTitle("Portrait"));
            results.Children.Add(_port);
            var scroll = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_save);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(_summary);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(footer);
            root.Children.Add(scroll);
            _w.Content = root;

            UpdateSummary();
            _w.SourceInitialized += (s, e) => Native.DarkTitleBar(new WindowInteropHelper(_w).Handle);
            _w.Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); _ = Search(); };
        }

        static TextBlock SectionTitle(string text) => new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 8)
        };

        async Task Search()
        {
            var term = _box.Text.Trim();
            if (term.Length == 0) return;
            int ver = ++_searchVer;
            _land.Children.Clear();
            _port.Children.Clear();
            _status.Text = $"Searching Steam, Microsoft Store and GOG for “{term}”…";

            // All stores at once; each returns null if it couldn't be reached.
            var steamTask = SteamCandidates(term);
            var msTask = Covers.SearchMicrosoft(term);
            var gogTask = Covers.SearchGog(term);
            await Task.WhenAll(steamTask, msTask, gogTask);
            if (ver != _searchVer) return;

            var sources = new (string name, List<Covers.ArtCandidate> found)[]
            {
                ("Steam", steamTask.Result), ("Microsoft Store", msTask.Result), ("GOG", gogTask.Result)
            };
            var all = sources.Where(s => s.found != null).SelectMany(s => s.found).ToList();
            if (all.Count == 0)
            {
                _status.Text = sources.All(s => s.found == null)
                    ? "Couldn't reach the stores. Check your connection and try again."
                    : $"Nothing found for “{term}”. Try a shorter or different name.";
                return;
            }

            _status.Text = "Loading images…";
            await Task.WhenAll(all.Select(c => AddTile(c.Tall ? _port : _land, ver, $"{c.Title}  ·  {c.Source}", c.Urls, c.Tall)));
            if (ver != _searchVer) return;

            string Count(string name, List<Covers.ArtCandidate> found) =>
                found == null ? $"{name}: couldn't connect"
                              : $"{name}: {found.Select(c => c.Title).Distinct().Count()} {(found.Select(c => c.Title).Distinct().Count() == 1 ? "game" : "games")}";
            _status.Text = _land.Children.Count + _port.Children.Count == 0
                ? "Those games have no cover art. Try another search."
                : string.Join("   ·   ", sources.Select(s => Count(s.name, s.found)));
        }

        /// <summary>Steam matches as picker candidates; null if Steam couldn't be reached.</summary>
        static async Task<List<Covers.ArtCandidate>> SteamCandidates(string term)
        {
            List<(int id, string name)> apps;
            try { apps = await Covers.SearchSteamAll(term); }
            catch { return null; }
            var top = apps.Take(8).ToList();
            // Newer games keep their art under hashed URLs, so get the real ones (one call for all).
            var assets = await Covers.GetAssets(top.Select(a => a.id));
            var list = new List<Covers.ArtCandidate>();
            foreach (var (id, name) in top)
            {
                assets.TryGetValue(id, out var a);
                var land = a?.LandscapeUrls is { Length: > 0 } l ? l : Covers.LandscapeUrls(id);
                var port = a?.PortraitUrls is { Length: > 0 } p ? p : Covers.PortraitUrls(id);
                foreach (var url in land) list.Add(new Covers.ArtCandidate("Steam", name, new[] { url }, false));
                list.Add(new Covers.ArtCandidate("Steam", name, port, true));
            }
            return list;
        }

        /// <summary>Adds a placeholder in result order, fills it when the image arrives, removes it if there's no image.</summary>
        async Task AddTile(WrapPanel panel, int ver, string name, string[] urls, bool tall)
        {
            double w = tall ? PortW : LandW, h = tall ? PortH : LandH;
            var img = new Image { Stretch = Stretch.Uniform, Width = w, Height = h };
            var loading = new TextBlock
            {
                Text = "Loading…",
                Foreground = Res("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var check = new Border
            {
                Background = Res("AccentBrush"),
                CornerRadius = new CornerRadius(11),
                Width = 22,
                Height = 22,
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "", // check mark
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var art = new Grid { Width = w, Height = h, Background = TileBg };
            art.Children.Add(loading);
            art.Children.Add(img);
            art.Children.Add(check);
            var caption = new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = Res("SecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = w,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = name
            };
            var stack = new StackPanel();
            stack.Children.Add(art);
            stack.Children.Add(caption);
            var tile = new Border
            {
                Child = stack,
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                IsEnabled = false,
                Tag = check
            };
            panel.Children.Add(tile);

            byte[] bytes = null;
            foreach (var u in urls)
                if ((bytes = await Covers.TryGetBytes(u)) != null) break;
            if (ver != _searchVer) return;
            var src = bytes == null ? null : await Task.Run(() => Covers.LoadImage(bytes, tall ? 300 : 480));
            if (ver != _searchVer) return;
            if (src == null) { panel.Children.Remove(tile); return; }

            img.Source = src;
            loading.Visibility = Visibility.Collapsed;
            tile.IsEnabled = true;
            tile.ToolTip = $"{name}  ·  {((System.Windows.Media.Imaging.BitmapSource)src).PixelWidth} px wide preview\nClick to use as the {(tall ? "portrait" : "landscape")} cover";
            tile.MouseEnter += (s, e) => { if (!IsSelected(tile)) tile.BorderBrush = HoverBorder; };
            tile.MouseLeave += (s, e) => { if (!IsSelected(tile)) tile.BorderBrush = Brushes.Transparent; };
            tile.MouseLeftButtonUp += (s, e) => Select(tile, tall, bytes, name);
        }

        bool IsSelected(Border t) => t == _selLand || t == _selPort;

        void Select(Border tile, bool tall, byte[] bytes, string name)
        {
            var prev = tall ? _selPort : _selLand;
            if (prev != null) Mark(prev, false);
            if (prev == tile) // clicking the selected image again clears it
            {
                if (tall) { _selPort = null; Portrait = null; } else { _selLand = null; Landscape = null; }
            }
            else
            {
                Mark(tile, true);
                if (tall) { _selPort = tile; Portrait = bytes; _selPortName = name; }
                else { _selLand = tile; Landscape = bytes; _selLandName = name; }
            }
            UpdateSummary();
        }

        static void Mark(Border tile, bool on)
        {
            tile.BorderBrush = on ? Res("AccentBrush") : Brushes.Transparent;
            ((Border)tile.Tag).Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateSummary()
        {
            _summary.Text = $"Landscape: {(_selLand != null ? _selLandName : "unchanged")}   ·   Portrait: {(_selPort != null ? _selPortName : "unchanged")}";
            _save.IsEnabled = _selLand != null || _selPort != null;
        }
    }
}
