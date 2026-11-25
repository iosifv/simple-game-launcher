using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>A newer release on GitHub: its version, the exe to download and the release page.</summary>
    public class UpdateInfo
    {
        public Version Version;
        public string DownloadUrl, PageUrl;
    }

    /// <summary>Checks GitHub for a newer release and replaces the running exe with it. The running exe can be renamed
    /// (not overwritten) on Windows, so: download to "GameLauncher.exe.new", rename the current one to ".old", move the
    /// new one into place, start it and exit; the next start deletes the ".old".</summary>
    public static class Updater
    {
        public const string Repo = "iosifv/simple-game-launcher";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

        static readonly HttpClient Http = CreateClient();
        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleGameLauncher-Updater");
            return c;
        }

        /// <summary>This build's version (from the csproj / the release tag), without the "+commit" suffix.</summary>
        public static Version Current { get; } = ParseVersion(
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            ?? new Version(0, 0);

        public static string CurrentText => Current.ToString(3);

        /// <summary>Only the published single-file exe can replace itself; a Debug build (exe + dlls) just opens the
        /// release page instead.</summary>
        public static bool CanSelfUpdate =>
            string.IsNullOrEmpty(typeof(Updater).Assembly.Location) && Environment.ProcessPath != null;

        static string ExePath => Environment.ProcessPath;
        static string NewPath => ExePath + ".new";
        static string OldPath => ExePath + ".old";

        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            return Version.TryParse(s, out var v) ? Normalize(v) : null;
        }

        static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>The latest release if it is newer than this build, else null. Throws when GitHub can't be reached.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            using var resp = await Http.GetAsync(LatestApi);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var latest = ParseVersion(root.GetProperty("tag_name").GetString());
            if (latest == null || latest <= Current) return null;
            string url = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), "GameLauncher.exe", StringComparison.OrdinalIgnoreCase))
                    url = a.GetProperty("browser_download_url").GetString();
            // Only ever download from GitHub itself.
            if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "github.com"))
                url = null;
            return new UpdateInfo
            {
                Version = latest,
                DownloadUrl = url,
                PageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : ReleasesPage
            };
        }

        /// <summary>Downloads the new exe and swaps it in. Returns the path to start; throws (leaving the current exe in
        /// place) if anything fails. progress gets 0..1, or -1 when the size is unknown.</summary>
        public static async Task<string> DownloadAndInstallAsync(UpdateInfo info, IProgress<double> progress)
        {
            if (info.DownloadUrl == null) throw new InvalidOperationException("This release has no GameLauncher.exe to download.");
            TryDelete(NewPath);
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = new FileStream(NewPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    done += n;
                    progress?.Report(total > 0 ? (double)done / total : -1);
                }
            }

            // Make sure it really is the version we asked for before replacing anything.
            var got = ParseVersion(FileVersionInfo.GetVersionInfo(NewPath).ProductVersion);
            if (got != info.Version)
            {
                TryDelete(NewPath);
                throw new InvalidDataException($"The download is version {got?.ToString(3) ?? "unknown"}, expected {info.Version.ToString(3)}.");
            }

            TryDelete(OldPath);
            File.Move(ExePath, OldPath);
            try { File.Move(NewPath, ExePath); }
            catch
            {
                File.Move(OldPath, ExePath); // put the working exe back
                throw;
            }
            return ExePath;
        }

        /// <summary>Removes what an update left behind (the old exe, a half-finished download).</summary>
        public static void CleanUp()
        {
            if (Environment.ProcessPath == null) return;
            Task.Run(async () =>
            {
                for (int i = 0; i < 10 && (File.Exists(OldPath) || File.Exists(NewPath)); i++)
                {
                    TryDelete(OldPath);
                    TryDelete(NewPath);
                    await Task.Delay(1000);
                }
            });
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
