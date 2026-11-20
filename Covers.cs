using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher
{
    /// <summary>Steam cover art: downloaded once, cached in the covers folder next to the exe.</summary>
    static class Covers
    {
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
            return c;
        }

        public static string CachedPath(int appId) => Path.Combine(Store.CoverDir, $"steam_{appId}.jpg");
        public static string CachedPortraitPath(int appId) => Path.Combine(Store.CoverDir, $"steam_{appId}_portrait.jpg");

        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> NoPortrait = new();

        /// <summary>Steam "library capsule" (600×900). Not every app has one; misses are remembered for this session.</summary>
        public static async Task<string> EnsureSteamPortrait(int appId)
        {
            var path = CachedPortraitPath(appId);
            if (File.Exists(path)) return path;
            if (NoPortrait.ContainsKey(appId)) return null;
            string[] urls =
            {
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg"
            };
            foreach (var u in urls)
                if (await TryDownload(u, path)) return path;
            // Newer apps keep their art under hashed paths; ask Steam for the real ones.
            if ((await GetAssets(new[] { appId })).TryGetValue(appId, out var a))
                foreach (var u in a.PortraitUrls)
                    if (await TryDownload(u, path)) return path;
            NoPortrait[appId] = true;
            return null;
        }

        public static string CachedHeroPath(int appId) => Path.Combine(Store.CoverDir, $"steam_{appId}_hero.jpg");

        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> NoHero = new();

        /// <summary>Steam "library hero" (wide 3840×1240 background art) for the hero background. Misses are remembered
        /// for this session.</summary>
        public static async Task<string> EnsureSteamHero(int appId)
        {
            var path = CachedHeroPath(appId);
            if (File.Exists(path)) return path;
            if (NoHero.ContainsKey(appId)) return null;
            if (await TryDownload($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg", path))
                return path;
            if ((await GetAssets(new[] { appId })).TryGetValue(appId, out var a) && a.Hero != null && await TryDownload(a.Hero, path))
                return path;
            NoHero[appId] = true;
            return null;
        }

        /// <summary>Real art URLs for a Steam app. Older apps use fixed file names, newer ones hashed paths.</summary>
        public class SteamAssets
        {
            public string Header, Capsule, Library, Library2x, Hero;
            public string[] LandscapeUrls => new[] { Header, Capsule }.Where(u => u != null).ToArray();
            public string[] PortraitUrls => new[] { Library2x, Library }.Where(u => u != null).ToArray();
        }

        /// <summary>Asset URLs for several apps in one call (IStoreBrowseService, no key needed). Missing apps are left out.</summary>
        public static async Task<Dictionary<int, SteamAssets>> GetAssets(IEnumerable<int> appIds)
        {
            var result = new Dictionary<int, SteamAssets>();
            var ids = appIds.Distinct().ToList();
            if (ids.Count == 0) return result;
            try
            {
                var input = JsonSerializer.Serialize(new
                {
                    ids = ids.Select(i => new { appid = i }),
                    context = new { language = "english", country_code = "US" },
                    data_request = new { include_assets = true }
                });
                var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("response", out var resp) || !resp.TryGetProperty("store_items", out var items)) return result;
                foreach (var it in items.EnumerateArray())
                {
                    if (!it.TryGetProperty("appid", out var idEl) || !it.TryGetProperty("assets", out var assets)) continue;
                    if (!assets.TryGetProperty("asset_url_format", out var fmtEl)) continue;
                    var fmt = "https://shared.cloudflare.steamstatic.com/store_item_assets/" + fmtEl.GetString();
                    string U(string prop) => assets.TryGetProperty(prop, out var v) && v.GetString() is string f && f.Length > 0
                        ? fmt.Replace("${FILENAME}", f) : null;
                    result[idEl.GetInt32()] = new SteamAssets
                    {
                        Header = U("header"),
                        Capsule = U("main_capsule"),
                        Library = U("library_capsule"),
                        Library2x = U("library_capsule_2x"),
                        Hero = U("library_hero")
                    };
                }
            }
            catch { }
            return result;
        }

        public static async Task<string> EnsureSteamCover(int appId)
        {
            var path = CachedPath(appId);
            if (File.Exists(path)) return path;

            string[] urls =
            {
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg"
            };
            foreach (var u in urls)
                if (await TryDownload(u, path)) return path;

            // Newer apps use hashed asset URLs; ask Steam for the exact one.
            if ((await GetAssets(new[] { appId })).TryGetValue(appId, out var a) && a.Header != null && await TryDownload(a.Header, path))
                return path;
            // Older fallback.
            try
            {
                var json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(appId.ToString(), out var app) &&
                    app.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("header_image", out var hdr) &&
                    await TryDownload(hdr.GetString(), path))
                    return path;
            }
            catch { }
            return null;
        }

        static async Task<bool> TryDownload(string url, string path)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return false;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 2000) return false;
                await File.WriteAllBytesAsync(path, bytes);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Looks a game up on the Steam store by exact (normalized) name. ok=false means the lookup itself failed.</summary>
        public static async Task<(bool ok, int? id)> SearchSteam(string name)
        {
            try
            {
                var url = "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(name);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var key = Library.Normalize(name);
                if (doc.RootElement.TryGetProperty("items", out var items))
                    foreach (var it in items.EnumerateArray())
                        if (Library.Normalize(it.GetProperty("name").GetString()) == key)
                            return (true, it.GetProperty("id").GetInt32());
                return (true, null);
            }
            catch { return (false, null); }
        }

        /// <summary>Steam store matches for a search term (not just exact names), for the cover picker.</summary>
        public static async Task<System.Collections.Generic.List<(int id, string name)>> SearchSteamAll(string term)
        {
            var list = new System.Collections.Generic.List<(int, string)>();
            var url = "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(term);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var it in items.EnumerateArray())
                    list.Add((it.GetProperty("id").GetInt32(), it.GetProperty("name").GetString()));
            return list;
        }

        /// <summary>Candidate art URLs for one Steam app, by shape. Some may not exist; the picker hides those.</summary>
        public static string[] LandscapeUrls(int appId) =>
        [
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_616x353.jpg"
        ];

        /// <summary>Same image at two sizes: the picker uses the first that exists.</summary>
        public static string[] PortraitUrls(int appId) =>
        [
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg"
        ];

        // ---------- other stores (no API keys) ----------

        /// <summary>One image the picker can offer. Urls are alternatives for the same picture (first that loads wins).</summary>
        public record ArtCandidate(string Source, string Title, string[] Urls, bool Tall);

        static string Https(string u) => u == null ? null : u.StartsWith("//") ? "https:" + u : u;

        /// <summary>Microsoft Store / Xbox games: Poster (2:3) for portrait, TitledHeroArt / SuperHeroArt (16:9) for landscape.
        /// Returns null when the store couldn't be reached.</summary>
        public static async Task<List<ArtCandidate>> SearchMicrosoft(string term, int max = 5)
        {
            try
            {
                var suggest = "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest?market=US&languages=en-US&productFamilyNames=Games&query="
                              + Uri.EscapeDataString(term);
                var ids = new List<string>();
                using (var doc = JsonDocument.Parse(await Http.GetStringAsync(suggest)))
                    if (doc.RootElement.TryGetProperty("Results", out var results))
                        foreach (var r in results.EnumerateArray())
                            if (r.TryGetProperty("Products", out var products))
                                foreach (var p in products.EnumerateArray())
                                    if (p.TryGetProperty("ProductId", out var pid)) ids.Add(pid.GetString());
                var list = new List<ArtCandidate>();
                if (ids.Count == 0) return list;

                var url = $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={string.Join(",", ids.Take(12))}&market=US&languages=en-US";
                using var details = JsonDocument.Parse(await Http.GetStringAsync(url));
                foreach (var p in details.RootElement.GetProperty("Products").EnumerateArray())
                {
                    // Skip DLC, currency packs and other add-ons.
                    if (!p.TryGetProperty("ProductType", out var type) || type.GetString() != "Game") continue;
                    var lp = p.GetProperty("LocalizedProperties")[0];
                    var title = lp.GetProperty("ProductTitle").GetString();
                    string Img(string purpose, int w)
                    {
                        foreach (var i in lp.GetProperty("Images").EnumerateArray())
                            if (i.GetProperty("ImagePurpose").GetString() == purpose) return Https(i.GetProperty("Uri").GetString()) + $"?w={w}";
                        return null;
                    }
                    var titled = Img("TitledHeroArt", 920);
                    var hero = Img("SuperHeroArt", 920);
                    var poster = Img("Poster", 600);
                    if (titled != null) list.Add(new ArtCandidate("Microsoft Store", title, new[] { titled }, false));
                    if (hero != null) list.Add(new ArtCandidate("Microsoft Store", title, new[] { hero }, false));
                    if (poster != null) list.Add(new ArtCandidate("Microsoft Store", title, new[] { poster }, true));
                    if (list.Select(c => c.Title).Distinct().Count() >= max) break;
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>GOG games: vertical and horizontal covers. GOG returns filler games when nothing matches,
        /// so only titles that contain the search (or are contained in it) are kept. Null when unreachable.</summary>
        public static async Task<List<ArtCandidate>> SearchGog(string term, int max = 5)
        {
            try
            {
                var url = "https://catalog.gog.com/v1/catalog?limit=20&productType=in:game,pack&order=desc:score&query=like:" + Uri.EscapeDataString(term);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var key = Library.Normalize(term);
                var list = new List<ArtCandidate>();
                int games = 0;
                foreach (var p in doc.RootElement.GetProperty("products").EnumerateArray())
                {
                    var title = p.GetProperty("title").GetString();
                    var t = Library.Normalize(title);
                    if (key.Length == 0 || t.Length == 0 || !(t.Contains(key) || key.Contains(t))) continue;
                    string Str(string prop) => p.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var wide = Str("coverHorizontal");
                    var tall = Str("coverVertical");
                    if (wide != null) list.Add(new ArtCandidate("GOG", title, new[] { wide }, false));
                    if (tall != null) list.Add(new ArtCandidate("GOG", title, new[] { tall }, true));
                    if (++games >= max) break;
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>Automatic covers for games Steam doesn't have: the first Microsoft Store or GOG game whose name matches exactly.
        /// ok=false means a store couldn't be reached (try again later).</summary>
        public static async Task<(bool ok, string wide, string tall)> FindOtherStoreArt(string name)
        {
            var key = Library.Normalize(name);
            bool ok = true;
            foreach (var search in new Func<string, Task<List<ArtCandidate>>>[] { t => SearchMicrosoft(t), t => SearchGog(t) })
            {
                var found = await search(name);
                if (found == null) { ok = false; continue; }
                var match = found.Where(c => Library.Normalize(c.Title) == key).ToList();
                var wide = match.FirstOrDefault(c => !c.Tall)?.Urls[0];
                var tall = match.FirstOrDefault(c => c.Tall)?.Urls[0];
                if (wide != null || tall != null) return (true, wide, tall);
            }
            return (ok, null, null);
        }

        public static string StoreCachedPath(string gameKey, bool tall) =>
            Path.Combine(Store.CoverDir, $"store_{Library.Normalize(gameKey)}{(tall ? "_portrait" : "")}.jpg");

        /// <summary>Downloads other-store art for a game into the cache. Returns false only when a store was unreachable.</summary>
        public static async Task<bool> EnsureOtherStoreArt(string gameKey, string name)
        {
            var (ok, wide, tall) = await FindOtherStoreArt(name);
            if (wide != null) await TryDownload(wide, StoreCachedPath(gameKey, false));
            if (tall != null) await TryDownload(tall, StoreCachedPath(gameKey, true));
            return ok;
        }

        /// <summary>Image bytes, or null if missing / too small to be real art.</summary>
        public static async Task<byte[]> TryGetBytes(string url)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                return bytes.Length < 2000 ? null : bytes;
            }
            catch { return null; }
        }

        public static ImageSource LoadImage(byte[] bytes, int decodeWidth)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.StreamSource = new MemoryStream(bytes);
                b.DecodePixelWidth = decodeWidth;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        /// <summary>Average colour weighted towards saturated, bright pixels (so a dark cover with a red logo glows red,
        /// not muddy brown), then lifted so it reads as light on the dark background. Grey covers give a soft white.</summary>
        public static Color? VividColor(System.Windows.Media.Imaging.BitmapSource src)
        {
            try
            {
                // Sample at most ~48 px wide; covers decoded for the backdrop are already 24 px.
                System.Windows.Media.Imaging.BitmapSource bmp = src;
                if (bmp.PixelWidth > 48)
                    bmp = new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(48.0 / bmp.PixelWidth, 48.0 / bmp.PixelWidth));
                bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                var px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);

                double r = 0, gr = 0, b = 0, total = 0, satSum = 0, n = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue; // transparent icon background
                    double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                    double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                    double sat = max <= 0 ? 0 : (max - min) / max;
                    double weight = 0.02 + sat * sat * max;     // vivid and bright pixels dominate
                    r += pr * weight; gr += pg * weight; b += pb * weight; total += weight;
                    satSum += sat; n++;
                }
                if (n == 0 || total <= 0) return null;
                r /= total; gr /= total; b /= total;

                // To HSV, then make it glow-worthy.
                double mx = Math.Max(r, Math.Max(gr, b)), mn = Math.Min(r, Math.Min(gr, b));
                double hue = 0, s = mx <= 0 ? 0 : (mx - mn) / mx;
                if (mx - mn > 1e-6)
                {
                    if (mx == r) hue = 60 * (((gr - b) / (mx - mn)) % 6);
                    else if (mx == gr) hue = 60 * ((b - r) / (mx - mn) + 2);
                    else hue = 60 * ((r - gr) / (mx - mn) + 4);
                }
                if (hue < 0) hue += 360;
                bool greyCover = satSum / n < 0.12;
                s = greyCover ? 0.08 : Math.Clamp(s * 1.25, 0.5, 0.9);
                double v = greyCover ? 0.9 : 1.0;
                return FromHsv(hue, s, v);
            }
            catch { return null; }
        }

        public static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
                          : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        /// <summary>Backdrop for cards without a cover: a radial glow in the icon's colour, bright-ish in the middle
        /// and fading to near black at the edges (the look of the hand-made AHK / SCO tiles). Null if no icon.</summary>
        public static Brush IconBackdrop(ImageSource icon)
        {
            if (icon is not BitmapSource bmp || VividColor(bmp) is not Color c) return null;
            static Color Shade(Color x, double f, byte floor) => Color.FromRgb(
                (byte)Math.Max(floor, x.R * f), (byte)Math.Max(floor, x.G * f), (byte)Math.Max(floor, x.B * f));
            var brush = new RadialGradientBrush
            {
                Center = new System.Windows.Point(0.5, 0.5),
                GradientOrigin = new System.Windows.Point(0.5, 0.5),
                RadiusX = 0.75,
                RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop(Shade(c, 0.46, 14), 0),
                    new GradientStop(Shade(c, 0.24, 12), 0.55),
                    new GradientStop(Shade(c, 0.08, 10), 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        public static ImageSource LoadImage(string path) => LoadImage(path, 460);

        /// <summary>decodeWidth ~24 gives a tiny image that, stretched, works as a cheap blur.</summary>
        public static ImageSource LoadImage(string path, int decodeWidth)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = decodeWidth;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }
}
