using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncher
{
    public class ScanEntry
    {
        public string Key, FilePath, Name, SortKey, Folder;
        public int? SteamAppId;
        /// <summary>Where the game comes from ("Steam", "Epic", "Xbox", …), worked out from the shortcut's target; null = unknown.</summary>
        public string StoreName;
        public DateTime LastWrite;
    }

    public class ScanResult
    {
        public bool RootExists;
        public List<ScanEntry> Games = new List<ScanEntry>();
        public List<ScanEntry> Launchers = new List<ScanEntry>();
        public List<string> Trainers = new List<string>();
    }

    /// <summary>Reads the shortcuts folder. Pure file-system work, safe to run off the UI thread.</summary>
    public static class Library
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        static readonly Regex SteamRx = new Regex(@"^URL=steam://(?:rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        static readonly Regex TrainerRx = new Regex(@"^(.*?)\s+(?:v\d|plus\s*\d|trainer\b|update|\(|-\s)", RegexOptions.IgnoreCase);
        static readonly Regex ShortcutSuffix = new Regex(@"\s*-\s*shortcut$", RegexOptions.IgnoreCase);
        static readonly Regex LeadingNumber = new Regex(@"^\d+\s*[\.\)\-]\s*");

        static readonly EnumerationOptions Flat = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        static readonly EnumerationOptions Deep = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

        public static bool IsShortcut(string p)
        {
            var e = Path.GetExtension(p);
            return e.Equals(".lnk", OIC) || e.Equals(".url", OIC);
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant())
                if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>"Black Myth Wukong" -> "bmw", "TheFloorAbove" -> "tfa".</summary>
        public static string InitialsOf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            char prev = ' ';
            foreach (var c in s)
            {
                if (c < 128 && char.IsLetterOrDigit(c) &&
                    (!(prev < 128 && char.IsLetterOrDigit(prev)) || (char.IsUpper(c) && char.IsLower(prev))))
                    sb.Append(char.ToLowerInvariant(c));
                prev = c;
            }
            return sb.ToString();
        }

        public static string DisplayName(string file)
        {
            var raw = Path.GetFileNameWithoutExtension(file);
            var n = LeadingNumber.Replace(raw, "").Replace("™", "").Replace("®", "").Trim();
            return n.Length > 0 ? n : raw;
        }

        public static string KeyFor(string root, string path)
        {
            string rel;
            try { rel = Path.GetRelativePath(root, path); } catch { return null; }
            if (rel == "." || rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
            if (IsShortcut(rel)) rel = Path.ChangeExtension(rel, null);
            return rel;
        }

        public static string TrainerKey(string path)
        {
            var name = ShortcutSuffix.Replace(Path.GetFileNameWithoutExtension(path), "");
            var m = TrainerRx.Match(name);
            return Normalize(m.Success ? m.Groups[1].Value : name);
        }

        public static ScanResult Scan(string root)
        {
            var r = new ScanResult();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return r;
            r.RootExists = true;

            foreach (var f in Files(root, Flat))
                if (IsShortcut(f)) r.Games.Add(Entry(root, f, ""));

            foreach (var dir in Dirs(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("Launchers", OIC))
                {
                    foreach (var f in Files(dir, Deep))
                        if (IsShortcut(f) || f.EndsWith(".exe", OIC)) r.Launchers.Add(Entry(root, f, ""));
                }
                else if (name.Equals("z", OIC))
                {
                    foreach (var f in Files(dir, Deep))
                    {
                        // Programs need "trainer" in the name (so helper tools like an auto-clicker stay out);
                        // shortcuts (.lnk / .url) were put here on purpose and always count, e.g. "Starcraft.lnk".
                        var ext = Path.GetExtension(f);
                        bool trainerExe = ext.Equals(".exe", OIC) && Path.GetFileName(f).Contains("trainer", OIC);
                        if (trainerExe || IsShortcut(f)) r.Trainers.Add(f);
                    }
                }
                else if (!name.Equals("ICOS", OIC))
                {
                    foreach (var f in Files(dir, Deep))
                        if (IsShortcut(f)) r.Games.Add(Entry(root, f, name));
                }
            }
            return r;
        }

        static ScanEntry Entry(string root, string file, string folder)
        {
            var e = new ScanEntry
            {
                Key = KeyFor(root, file),
                FilePath = file,
                Name = DisplayName(file),
                SortKey = Path.GetFileNameWithoutExtension(file),
                Folder = folder
            };
            try { e.LastWrite = File.GetLastWriteTimeUtc(file); } catch { }
            if (file.EndsWith(".url", OIC))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var m = SteamRx.Match(text);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var id)) e.SteamAppId = id;
                    e.StoreName = StoreOf(text.ToLowerInvariant());
                }
                catch { }
            }
            else
            {
                try { e.StoreName = StoreOf(LinkText(file)); } catch { }
            }
            if (e.SteamAppId != null) e.StoreName = "Steam";
            return e;
        }

        /// <summary>The strings inside a .lnk (target path, arguments, the app id of a Store app), lower case. A .lnk
        /// keeps them as ANSI or UTF-16, so the bytes are read both ways; enough to spot a store's folder or link.</summary>
        static string LinkText(string file)
        {
            byte[] b;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                b = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
                int read = 0, n;
                while (read < b.Length && (n = fs.Read(b, read, b.Length - read)) > 0) read += n;
            }
            var sb = new StringBuilder();
            sb.Append(Encoding.Latin1.GetString(b)).Append('\n');
            sb.Append(Encoding.Unicode.GetString(b)).Append('\n');
            if (b.Length > 1) sb.Append(Encoding.Unicode.GetString(b, 1, b.Length - 1));
            return sb.ToString().ToLowerInvariant();
        }

        static readonly (string store, string[] marks)[] Stores =
        {
            ("Steam", new[] { "steam://", @"\steamapps\", @"\steam\steam.exe" }),
            ("Epic", new[] { "com.epicgames.launcher://", @"\epic games\" }),
            ("GOG", new[] { "goggalaxy://", @"\gog galaxy\", @"\gog games\", @"\gog.com\" }),
            ("EA", new[] { "link2ea://", "origin://", "origin2://", @"\ea games\", @"\electronic arts\", @"\origin games\" }),
            ("Ubisoft", new[] { "uplay://", @"\ubisoft game launcher\", @"\ubisoft\" }),
            ("Battle.net", new[] { "battlenet://", @"\battle.net\", @"\blizzard" }),
            ("Riot", new[] { @"\riot games\", "riotclient" }),
            ("Rockstar", new[] { @"\rockstar games\" }),
            ("Amazon", new[] { "amazon-games://", @"\amazon games\" }),
            ("HoYoPlay", new[] { @"\hoyoplay\", "hyp_global", "hoyoplay" }),
            ("Netmarble", new[] { @"\netmarble" }),
            ("Hydra", new[] { "hydralauncher://", @"\hydra\hydra.exe", @"\hydra launcher games kit\" }),
            ("Xbox", new[] { @"\xboxgames\", @"\windowsapps\", @"\modifiablewindowsapps\", "ms-xbl-", "xbox://" }),
        };
        // A Start-menu Store app keeps an app id like "Publisher.Name_8wekyb3d8bbwe!App".
        static readonly Regex StoreAppRx = new Regex(@"_[a-z0-9]{13}![a-z]", RegexOptions.IgnoreCase);

        static string StoreOf(string text)
        {
            foreach (var (store, marks) in Stores)
                foreach (var m in marks)
                    if (text.Contains(m)) return store;
            return StoreAppRx.IsMatch(text) ? "Xbox" : null;
        }

        static IEnumerable<string> Files(string dir, EnumerationOptions o)
        {
            try { return Directory.GetFiles(dir, "*", o); } catch { return Array.Empty<string>(); }
        }

        static IEnumerable<string> Dirs(string dir)
        {
            try { return Directory.GetDirectories(dir, "*", Flat); } catch { return Array.Empty<string>(); }
        }
    }
}
