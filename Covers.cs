using System;
using System.IO;
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

            // Newer apps use hashed asset URLs; ask the store for the exact one.
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

        public static ImageSource LoadImage(string path)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = 460;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }
}
