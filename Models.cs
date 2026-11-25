using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameLauncher
{
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class GameItem : Notify
    {
        public string Key { get; set; }
        public string SortKey { get; set; }
        public DateTime LastWrite { get; set; }
        public int? SteamAppId { get; set; }
        public int ArtVersion;
        public string SearchKey { get; private set; } = "";
        public string Initials { get; private set; } = "";

        string _filePath, _name, _folder, _trainer;
        ImageSource _icon, _cover;
        List<string> _tags = new List<string>();
        DateTime? _lastPlayed;

        public string FilePath { get => _filePath; set => Set(ref _filePath, value); }
        public string Name
        {
            get => _name;
            set { if (Set(ref _name, value)) { SearchKey = Library.Normalize(value); Initials = Library.InitialsOf(value); } }
        }
        public string Folder { get => _folder; set { if (Set(ref _folder, value)) Raise(nameof(SubText)); } }
        public ImageSource Icon { get => _icon; set => Set(ref _icon, value); }
        Brush _iconBackdrop;
        /// <summary>Radial glow in the icon's colour, shown behind the icon on cards without a cover.</summary>
        public Brush IconBackdrop { get => _iconBackdrop; set => Set(ref _iconBackdrop, value); }
        public ImageSource Cover { get => _cover; set { if (Set(ref _cover, value)) Raise(nameof(HasCover)); } }
        public bool HasCover => _cover != null;

        ImageSource _coverFill;
        bool _coverFits = true;
        /// <summary>Tiny, upscaled copy of the cover: a soft blurred backdrop when the cover's shape doesn't match the card.</summary>
        public ImageSource CoverFill { get => _coverFill; set => Set(ref _coverFill, value); }
        /// <summary>False when the cover is the other orientation (e.g. a landscape header on a portrait card): it's shown whole, letterboxed.</summary>
        public bool CoverFits { get => _coverFits; set => Set(ref _coverFits, value); }
        public string Trainer
        {
            get => _trainer;
            set { if (Set(ref _trainer, value)) { Raise(nameof(HasTrainer)); Raise(nameof(TrainerTip)); Raise(nameof(PlayTip)); } }
        }
        public bool HasTrainer => !string.IsNullOrEmpty(_trainer);
        public string TrainerTip => HasTrainer ? "Play with trainer: " + Path.GetFileNameWithoutExtension(_trainer) : null;
        public string PlayTip => HasTrainer ? "Play  ·  Ctrl+click: play with trainer" : "Play";
        public List<string> Tags { get => _tags; set { _tags = value ?? new List<string>(); Raise(nameof(Tags)); Raise(nameof(SubText)); } }
        public DateTime? LastPlayed { get => _lastPlayed; set { if (Set(ref _lastPlayed, value)) Raise(nameof(SubText)); } }
        /// <summary>Hidden by the user (GameData.Hidden): only the "Hidden" filter shows it.</summary>
        public bool Hidden { get; set; }

        bool _arranging, _isDragging;
        public bool Arranging { get => _arranging; set => Set(ref _arranging, value); }
        public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

        public string SubText
        {
            get
            {
                var parts = new List<string>();
                if (_lastPlayed is DateTime d) parts.Add("Played " + Relative(d));
                if (!string.IsNullOrEmpty(_store)) parts.Add(_store);
                // The folder name only when the filter isn't already that folder.
                if (!string.IsNullOrEmpty(_folder) && !string.Equals(_folder, OpenFolder, StringComparison.OrdinalIgnoreCase)) parts.Add(_folder);
                if (_tags.Count > 0) parts.Add(string.Join(", ", _tags));
                return string.Join("  ·  ", parts);
            }
        }

        string _store;
        /// <summary>"Steam", "Epic", "Xbox", … from the shortcut's target (Library), null when unknown.</summary>
        public string StoreName { get => _store; set { if (Set(ref _store, value)) Raise(nameof(SubText)); } }

        /// <summary>The folder whose filter tab is open (null otherwise); SubText leaves that folder's name out.</summary>
        public static string OpenFolder;
        public void RefreshSubText() => Raise(nameof(SubText));

        static string Relative(DateTime d)
        {
            var days = (DateTime.Now.Date - d.Date).Days;
            if (days <= 0) return "today";
            if (days == 1) return "yesterday";
            if (days < 30) return days + " days ago";
            return d.ToString("d MMM yyyy");
        }
    }

    public class LauncherItem : Notify
    {
        ImageSource _icon;
        public string Name { get; set; }
        public string FilePath { get; set; }
        public DateTime LastWrite { get; set; }
        public ImageSource Icon { get => _icon; set => Set(ref _icon, value); }
    }

    public record TabVm(string Label, string Value)
    {
        /// <summary>Tag filters show a tag icon before the name (instead of a "#").</summary>
        public bool IsTag => Value.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);
        /// <summary>The "Hidden" filter shows a crossed-out eye before its name.</summary>
        public bool IsHidden => Value == "hidden";
    }

    public class WallpaperRow : Notify
    {
        bool _isCurrent;
        public string Label { get; set; }
        public string Key { get; set; }
        public bool IsTag => Key?.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) == true;
        public bool IsHidden => Key == "hidden";
        public string Note { get; set; }
        public ImageSource Thumb { get; set; }
        public bool HasImage => Thumb != null;
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    }

    public class GameData
    {
        public List<string> Tags { get; set; }
        /// <summary>null = auto-detect, "" = no trainer, otherwise a path.</summary>
        public string Trainer { get; set; }
        public DateTime? LastPlayed { get; set; }
        public int PlayCount { get; set; }
        public int? SteamAppId { get; set; }
        public bool SteamLookupDone { get; set; }
        /// <summary>Microsoft Store / GOG were searched for a game Steam doesn't have (results cached as covers\store_*.jpg).</summary>
        public bool StoreLookupDone { get; set; }
        public string CustomCover { get; set; }
        /// <summary>Custom cover used on portrait cards (a tall image).</summary>
        public string CustomCoverPortrait { get; set; }
        public bool NoCover { get; set; }
        /// <summary>Background art chosen by the user for the hero banner (a copy in covers\). Wins over everything automatic.</summary>
        public string CustomHero { get; set; }
        /// <summary>Hidden from every filter, search and the dice; listed only under the "Hidden" filter.</summary>
        public bool Hidden { get; set; }
    }

    public class Settings
    {
        /// <summary>The shortcuts folder to show. Empty on first run: the settings panel opens so the user can pick one.</summary>
        public string Folder { get; set; } = "";
        public string Hotkey { get; set; } = "Win+Shift+G";
        public int TrainerDelaySeconds { get; set; } = 10;
        public bool HideOnLaunch { get; set; } = true;
        public bool HideOnFocusLoss { get; set; } = false;
        public bool CloseToTray { get; set; } = true;
        public bool FetchCovers { get; set; } = true;
        public double Width { get; set; } = 1180;
        public double Height { get; set; } = 760;
        /// <summary>Last normal (not maximized) window bounds [left, top, right, bottom] in screen pixels; this also
        /// records which monitor it was on. Null = centre on the main screen (first start).</summary>
        public int[] WindowBounds { get; set; }
        /// <summary>The window was maximized (on the monitor of WindowBounds) when last closed / hidden.</summary>
        public bool Maximized { get; set; }
        /// <summary>The game you point at (or the selected one) shows its wide art behind everything, with its name above the filters.</summary>
        public bool HeroBackground { get; set; } = true;
        /// <summary>The accent colour (selected filter, selection ring, highlights) follows that game's cover colour.</summary>
        public bool AccentFromGame { get; set; } = true;
        /// <summary>Header shows only search, dice and a ⋯ menu (settings, rescan, open folder, exit).</summary>
        public bool CompactHeader { get; set; } = true;
        /// <summary>While a card is hovered, the other cards dim a little.</summary>
        public bool Spotlight { get; set; } = true;
        /// <summary>How much the other cards dim, in percent (10–90).</summary>
        public int SpotlightDim { get; set; } = 50;
        public double TileMinWidth { get; set; } = 220;
        public string DefaultTab { get; set; } = "games";
        /// <summary>Subfolders whose shortcuts are not games (excluded from the Games filter).</summary>
        public List<string> NonGameFolders { get; set; } = new List<string> { "Tools" };
        /// <summary>Filter value ("games", "all", "folder:Tools", "tag:coop", or "*" for default) → image path.</summary>
        public Dictionary<string, string> Wallpapers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Custom game order (keys). Empty = alphabetical. Games not listed go to the end.</summary>
        public List<string> CustomOrder { get; set; } = new List<string>();
        /// <summary>Custom filter-tab order (tab values). Empty = default order. Tabs not listed go to the end.</summary>
        public List<string> TabOrder { get; set; } = new List<string>();
        /// <summary>Custom launcher-bar order (shortcut file names without extension). Empty = alphabetical. Others go to the end.</summary>
        public List<string> LauncherOrder { get; set; } = new List<string>();
        /// <summary>"landscape" (Steam header art) or "portrait" (Steam library art).</summary>
        public string CardLayout { get; set; } = "landscape";
        public bool ShowCardText { get; set; } = true;
        /// <summary>Space between cards in px (each card gets half of it on every side).</summary>
        public int CardSpacing { get; set; } = 12;
        /// <summary>Mouse wheel scrolls the grid smoothly by exactly one row of cards.</summary>
        public bool ScrollByRow { get; set; }
        /// <summary>Colour of the random-game dice in the header (#RRGGBB).</summary>
        public string DiceColor { get; set; } = "#60CDFF";
        /// <summary>Start the game the dice lands on instead of just selecting it.</summary>
        public bool DiceStartsGame { get; set; }
        /// <summary>Hover tooltips (game names, play / trainer hints, button descriptions). Off: none anywhere.</summary>
        public bool ShowTips { get; set; } = true;
        /// <summary>Look for a newer release on GitHub at start and once a day (Updater).</summary>
        public bool CheckForUpdates { get; set; } = true;
        /// <summary>Install a found update by itself, but only while the launcher is hidden.</summary>
        public bool AutoInstallUpdates { get; set; } = false;
        public DateTime? LastUpdateCheck { get; set; }
        /// <summary>Trainers on/off. Off: no game gets a trainer, so badges, the "With trainer" filter,
        /// trainer menus and Ctrl+Enter-with-trainer all go away.</summary>
        public bool TrainersEnabled { get; set; } = true;
        public double WallpaperDim { get; set; } = 0.5;
        public double WallpaperBlur { get; set; } = 0;
        public Dictionary<string, GameData> Games { get; set; } = new Dictionary<string, GameData>(StringComparer.OrdinalIgnoreCase);
    }

    public static class Store
    {
        public static string Dir => AppContext.BaseDirectory;
        public static string FilePath => Path.Combine(Dir, "launcher-data.json");
        public static string CoverDir
        {
            get { var d = Path.Combine(Dir, "covers"); Directory.CreateDirectory(d); return d; }
        }
        public static string WallpaperDir
        {
            get { var d = Path.Combine(Dir, "wallpapers"); Directory.CreateDirectory(d); return d; }
        }
        public static Settings S { get; private set; } = new Settings();

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        static readonly object Lock = new object();

        public static void Load()
        {
            Settings s = null;
            try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opt); }
            catch { }
            s ??= new Settings();
            s.Games = new Dictionary<string, GameData>(s.Games ?? new Dictionary<string, GameData>(), StringComparer.OrdinalIgnoreCase);
            s.Wallpapers = new Dictionary<string, string>(s.Wallpapers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            s.NonGameFolders ??= new List<string> { "Tools" };
            s.CustomOrder ??= new List<string>();
            s.TabOrder ??= new List<string>();
            s.LauncherOrder ??= new List<string>();
            if (s.CardLayout != "portrait") s.CardLayout = "landscape";
            if (string.IsNullOrWhiteSpace(s.DefaultTab)) s.DefaultTab = "games";
            s.TileMinWidth = Math.Clamp(s.TileMinWidth <= 0 ? 220 : s.TileMinWidth, 150, 420);
            s.CardSpacing = Math.Clamp(s.CardSpacing, 0, 48);
            if (string.IsNullOrWhiteSpace(s.DiceColor)) s.DiceColor = "#60CDFF";
            S = s;
        }

        public static void Save()
        {
            lock (Lock)
            {
                try
                {
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(S, Opt));
                    File.Move(tmp, FilePath, true);
                }
                catch { }
            }
        }

        public static GameData Find(string key) => key != null && S.Games.TryGetValue(key, out var g) ? g : null;

        public static GameData Game(string key)
        {
            if (!S.Games.TryGetValue(key, out var g)) { g = new GameData(); S.Games[key] = g; }
            return g;
        }

        public static void Move(string oldKey, string newKey)
        {
            if (oldKey == null || newKey == null || string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
            if (S.Games.TryGetValue(oldKey, out var g) && !S.Games.ContainsKey(newKey))
            {
                S.Games[newKey] = g;
                S.Games.Remove(oldKey);
            }
            MoveOrderKey(oldKey, newKey);
        }

        public static void MoveOrderKey(string oldKey, string newKey)
        {
            int i = S.CustomOrder.FindIndex(k => string.Equals(k, oldKey, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && !S.CustomOrder.Contains(newKey, StringComparer.OrdinalIgnoreCase)) S.CustomOrder[i] = newKey;
        }
    }

    public static class StartupReg
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "GameLauncher";
        public static bool IsEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
        public static void Set(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
        }
    }
}
