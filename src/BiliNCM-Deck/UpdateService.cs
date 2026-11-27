using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiliNetEaseIntegratedApp
{
    internal sealed class UpdateState
    {
        public long lastCheckUtc { get; set; }
        public string dismissedVersion { get; set; } = "";
        public string latestVersion { get; set; } = "";
        public string releaseUrl { get; set; } = "";
        public string zipUrl { get; set; } = "";
        public string shaUrl { get; set; } = "";
        public string updaterUrl { get; set; } = "";
        public string notes { get; set; } = "";
        public string error { get; set; } = "";
    }

    internal sealed class UpdateCheckResult
    {
        public bool ok { get; set; }
        public bool hasUpdate { get; set; }
        public bool dismissed { get; set; }
        public string currentVersion { get; set; } = "";
        public string latestVersion { get; set; } = "";
        public string releaseUrl { get; set; } = "";
        public string zipUrl { get; set; } = "";
        public string shaUrl { get; set; } = "";
        public string updaterUrl { get; set; } = "";
        public string notes { get; set; } = "";
        public string error { get; set; } = "";
        public long lastCheckUtc { get; set; }
        public int pendingCount { get; set; }
        public int liveStatus { get; set; } = -1;
    }

    internal static class UpdateService
    {
        private const long CheckIntervalSeconds = 48 * 60 * 60;
        private static readonly object Sync = new object();
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "update-state.json");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static string CurrentVersion
        {
            get
            {
                var version = typeof(Program).Assembly.GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        public static async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken ct = default)
        {
            UpdateState state;
            lock (Sync) state = ReadStateUnlocked();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!force && state.lastCheckUtc > 0 &&
                now - state.lastCheckUtc < CheckIntervalSeconds &&
                !string.IsNullOrWhiteSpace(state.latestVersion))
            {
                return BuildResult(state);
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://api.github.com/repos/DDG-LAL/BiliNCM_Deck/releases/latest");
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("github_http_" + (int)response.StatusCode);
                string text = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                string tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
                string latest = tag.Trim().TrimStart('v', 'V');
                string releaseUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                string notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";
                string zipUrl = "";
                string shaUrl = "";
                string updaterUrl = "";

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                        string download = asset.TryGetProperty("browser_download_url", out var dlElement) ? dlElement.GetString() ?? "" : "";
                        if (name.StartsWith("BiliNCM-Deck-v", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            zipUrl = download;
                            shaUrl = download + ".sha256";
                        }
                        else if (name.Equals("BiliNCM-Deck-Updater.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            updaterUrl = download;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(zipUrl))
                    throw new InvalidOperationException("release_assets_missing");

                state.lastCheckUtc = now;
                state.latestVersion = latest;
                state.releaseUrl = releaseUrl;
                state.zipUrl = zipUrl;
                state.shaUrl = shaUrl;
                state.updaterUrl = updaterUrl;
                state.notes = notes;
                state.error = "";
                lock (Sync) SaveStateUnlocked(state);
                return BuildResult(state);
            }
            catch (Exception ex)
            {
                state.lastCheckUtc = now;
                state.error = ex.Message;
                lock (Sync) SaveStateUnlocked(state);
                return BuildResult(state);
            }
        }

        public static bool Dismiss(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            lock (Sync)
            {
                var state = ReadStateUnlocked();
                state.dismissedVersion = version.Trim().TrimStart('v', 'V');
                SaveStateUnlocked(state);
                return true;
            }
        }

        public static string ReleaseUrl
        {
            get
            {
                lock (Sync) return ReadStateUnlocked().releaseUrl;
            }
        }

        private static UpdateCheckResult BuildResult(UpdateState state)
        {
            string current = CurrentVersion;
            bool hasUpdate = CompareVersions(state.latestVersion, current) > 0;
            return new UpdateCheckResult
            {
                ok = string.IsNullOrWhiteSpace(state.error),
                hasUpdate = hasUpdate,
                dismissed = !string.IsNullOrWhiteSpace(state.latestVersion) &&
                    string.Equals(state.dismissedVersion, state.latestVersion, StringComparison.OrdinalIgnoreCase),
                currentVersion = current,
                latestVersion = state.latestVersion,
                releaseUrl = state.releaseUrl,
                zipUrl = state.zipUrl,
                shaUrl = state.shaUrl,
                updaterUrl = state.updaterUrl,
                notes = state.notes,
                error = state.error,
                lastCheckUtc = state.lastCheckUtc
            };
        }

        private static int CompareVersions(string left, string right)
        {
            var a = ParseVersion(left);
            var b = ParseVersion(right);
            for (int i = 0; i < 3; i++)
            {
                int result = a[i].CompareTo(b[i]);
                if (result != 0) return result;
            }
            return 0;
        }

        private static int[] ParseVersion(string value)
        {
            string clean = (value ?? "").Trim().TrimStart('v', 'V');
            int dash = clean.IndexOf('-');
            if (dash >= 0) clean = clean.Substring(0, dash);
            string[] parts = clean.Split('.');
            var result = new int[3];
            for (int i = 0; i < result.Length && i < parts.Length; i++)
                int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]);
            return result;
        }

        private static UpdateState ReadStateUnlocked()
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath), JsonOptions) ?? new UpdateState();
            }
            catch { }
            return new UpdateState();
        }

        private static void SaveStateUnlocked(UpdateState state)
        {
            try
            {
                File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
            }
            catch (Exception ex)
            {
                Program.WriteLog("[更新] 保存 update-state.json 失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "BiliNCM-Deck/" + CurrentVersion + " (+https://github.com/DDG-LAL/BiliNCM_Deck)");
            return client;
        }
    }
}
