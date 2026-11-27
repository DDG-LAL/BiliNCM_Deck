using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NeteaseCloudMusicApi;
using Newtonsoft.Json.Linq;

namespace BiliNetEaseIntegratedApp
{
    internal sealed class LyricsLine
    {
        public double time { get; set; }
        public string text { get; set; } = "";
        public string translation { get; set; } = "";
    }

    internal sealed class LyricsResult
    {
        public bool noLyrics { get; set; }
        public bool hasTimeline { get; set; }
        public string source { get; set; } = "";
        public string plainText { get; set; } = "";
        public List<LyricsLine> lines { get; set; } = new List<LyricsLine>();
        public string error { get; set; } = "";
    }

    internal sealed class LyricsConfig
    {
        public int lineCount { get; set; } = 5;
        public string skin { get; set; } = "default";
        public bool translation { get; set; } = false;
        public bool forceStatic { get; set; } = false;
        public string textColor { get; set; } = "#ffffff";

        public static readonly int[] AllowedLineCounts = { 1, 3, 5, 7 };
        public static readonly string[] AllowedSkins =
        {
            "default", "neon", "mist", "sakura", "pixel", "lyric", "pure"
        };

        public LyricsConfig Normalize()
        {
            return new LyricsConfig
            {
                lineCount = AllowedLineCounts.Contains(lineCount) ? lineCount : 5,
                skin = AllowedSkins.Contains(skin ?? "") ? skin : "default",
                translation = translation,
                forceStatic = forceStatic,
                textColor = Regex.IsMatch(textColor ?? "", "^#[0-9a-fA-F]{6}$") ? textColor : "#ffffff"
            };
        }
    }

    internal sealed class ParsedLyrics
    {
        public List<LyricsLine> Lines = new List<LyricsLine>();
        public string PlainText = "";
    }

    internal sealed class LyricsService
    {
        private sealed class CacheEntry
        {
            public LyricsResult Result = new LyricsResult();
            public DateTime ExpiresAt;
        }

        private static readonly Regex TimeTagRegex = new Regex(
            @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex OffsetRegex = new Regex(
            @"\[offset:([+-]?\d+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex MetaRegex = new Regex(
            @"^\[(ti|ar|al|by|offset|re|ve|length|language|tool|encoding):.*\]$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NormalizeRegex = new Regex(
            @"[\s\-_/\\.,，。·:：()（）\[\]【】]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly CloudMusicApi _api = CreateApi();
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private readonly SemaphoreSlim _fetchLock = new SemaphoreSlim(1, 1);

        public async Task<LyricsResult> GetAsync(string songId, string name, string artists, long durationMs, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(songId))
                return new LyricsResult { noLyrics = true, source = "none" };

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(songId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                    return cached.Result;
            }

            await _fetchLock.WaitAsync(ct);
            try
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(songId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                        return cached.Result;
                }

                LyricsResult result;
                try
                {
                    result = await FetchAsync(songId, name, artists, durationMs, ct);
                }
                catch (Exception ex)
                {
                    result = new LyricsResult
                    {
                        source = "error",
                        error = ex.Message,
                        noLyrics = true
                    };
                }

                int ttlMinutes = result.error.Length > 0 || result.noLyrics ? 2 : 30;
                lock (_cacheLock)
                {
                    _cache[songId] = new CacheEntry
                    {
                        Result = result,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes)
                    };
                }

                if (result.error.Length > 0)
                    Program.WriteLog($"lyrics: id={songId} source={result.source} error={result.error}", ConsoleColor.DarkGray);
                else
                    Program.WriteLog($"lyrics: id={songId} source={result.source} timed={result.hasTimeline} lines={result.lines.Count}", ConsoleColor.DarkGray);

                return result;
            }
            finally
            {
                _fetchLock.Release();
            }
        }

        private async Task<LyricsResult> FetchAsync(string songId, string name, string artists, long durationMs, CancellationToken ct)
        {
            JObject? response;
            try
            {
                response = await _api.RequestAsync(
                    CloudMusicApiProviders.Lyric,
                    new Dictionary<string, object> { { "id", songId } },
                    false);
            }
            catch
            {
                response = null;
            }

            // The encrypted provider can fail depending on region/cookies. The
            // public lyric endpoint is stable and returns the same lrc/tlyric.
            if (response == null || (response["lrc"]?["lyric"] == null && response["tlyric"]?["lyric"] == null))
            {
                var direct = await FetchNeteasePublicAsync(songId, ct);
                if (direct != null) response = direct;
            }

            if (response == null)
                return new LyricsResult { source = "error", error = "netease: empty response", noLyrics = true };

            string lrc = response["lrc"]?["lyric"]?.ToString() ?? "";
            string translated = response["tlyric"]?["lyric"]?.ToString() ?? "";
            bool noLyricFlag = response["nolyric"]?.Value<bool>() == true || response["uncollected"]?.Value<bool>() == true;

            ParsedLyrics parsed = ParseLrc(lrc);
            ParsedLyrics translation = ParseLrc(translated);

            if (parsed.Lines.Count == 0 && string.IsNullOrWhiteSpace(parsed.PlainText))
            {
                return new LyricsResult
                {
                    noLyrics = true,
                    source = noLyricFlag ? "netease_none" : "none"
                };
            }

            if (parsed.Lines.Count > 0)
            {
                AttachTranslationByTime(parsed.Lines, translation);
                return new LyricsResult
                {
                    source = "netease",
                    hasTimeline = true,
                    plainText = parsed.PlainText,
                    lines = parsed.Lines
                };
            }

            // NCM returned text but no timestamps. Try a synced external source first.
            List<LyricsLine>? external = await TryLrclibAsync(name, artists, durationMs, ct);
            if (external != null && external.Count > 0)
            {
                return new LyricsResult
                {
                    source = "lrclib",
                    hasTimeline = true,
                    plainText = parsed.PlainText,
                    lines = external
                };
            }

            // Last automatic fallback: distribute the untimed lines over the song duration.
            List<LyricsLine> estimated = Estimate(parsed.PlainText, durationMs);
            if (estimated.Count > 0)
            {
                AttachTranslationByIndex(estimated, translation);
                return new LyricsResult
                {
                    source = "estimated",
                    hasTimeline = true,
                    plainText = parsed.PlainText,
                    lines = estimated
                };
            }

            return new LyricsResult
            {
                source = "static",
                hasTimeline = false,
                plainText = parsed.PlainText
            };
        }

        internal static ParsedLyrics ParseLrc(string raw)
        {
            var result = new ParsedLyrics();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            double offsetSeconds = 0;
            var offsetMatch = OffsetRegex.Match(raw);
            if (offsetMatch.Success && double.TryParse(
                    offsetMatch.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var offsetMs))
            {
                offsetSeconds = offsetMs / 1000.0;
            }

            var timed = new List<LyricsLine>();
            var plain = new List<string>();
            foreach (var sourceLine in raw.Replace("\r", "").Split('\n'))
            {
                string line = sourceLine.Trim();
                if (line.Length == 0) continue;

                MatchCollection tags = TimeTagRegex.Matches(line);
                if (tags.Count == 0)
                {
                    if (MetaRegex.IsMatch(line)) continue;
                    string textOnly = WebUtility.HtmlDecode(line).Trim();
                    if (textOnly.Length > 0) plain.Add(textOnly);
                    continue;
                }

                int lastTagEnd = tags[tags.Count - 1].Index + tags[tags.Count - 1].Length;
                string content = WebUtility.HtmlDecode(line.Substring(lastTagEnd)).Trim();
                if (content.Length == 0) continue;

                plain.Add(content);
                foreach (Match tag in tags)
                {
                    double time = ParseTimestamp(tag) + offsetSeconds;
                    if (time < 0) time = 0;
                    timed.Add(new LyricsLine { time = time, text = content });
                }
            }

            result.Lines = timed
                .GroupBy(x => Math.Round(x.time, 2))
                .Select(g => new LyricsLine
                {
                    time = g.Key,
                    text = string.Join(" / ", g.Select(x => x.text).Distinct())
                })
                .OrderBy(x => x.time)
                .ToList();

            var plainLines = new List<string>();
            foreach (var line in plain)
            {
                if (plainLines.Count == 0 || plainLines[plainLines.Count - 1] != line)
                    plainLines.Add(line);
            }
            result.PlainText = string.Join("\n", plainLines);
            return result;
        }

        private static double ParseTimestamp(Match match)
        {
            int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int milliseconds = 0;
            if (match.Groups[3].Success)
            {
                string fraction = match.Groups[3].Value;
                int value = int.Parse(fraction, CultureInfo.InvariantCulture);
                milliseconds = fraction.Length == 1 ? value * 100
                    : fraction.Length == 2 ? value * 10
                    : value;
            }
            return minutes * 60 + seconds + milliseconds / 1000.0;
        }

        private static void AttachTranslationByTime(List<LyricsLine> lines, ParsedLyrics translation)
        {
            if (translation.Lines.Count == 0)
            {
                AttachTranslationByIndex(lines, translation);
                return;
            }

            foreach (var line in lines)
            {
                var nearest = translation.Lines
                    .Where(x => Math.Abs(x.time - line.time) <= 0.9)
                    .OrderBy(x => Math.Abs(x.time - line.time))
                    .FirstOrDefault();
                if (nearest != null)
                    line.translation = nearest.text;
            }
        }

        private static void AttachTranslationByIndex(List<LyricsLine> lines, ParsedLyrics translation)
        {
            var texts = translation.Lines.Count > 0
                ? translation.Lines.OrderBy(x => x.time).Select(x => x.text).ToList()
                : SplitPlainText(translation.PlainText);
            if (texts.Count != lines.Count) return;

            for (int i = 0; i < lines.Count; i++)
                lines[i].translation = texts[i];
        }

        private static List<LyricsLine> Estimate(string plainText, long durationMs)
        {
            var texts = SplitPlainText(plainText);
            if (texts.Count == 0 || durationMs <= 0) return new List<LyricsLine>();

            double totalSeconds = durationMs / 1000.0;
            var result = new List<LyricsLine>();
            for (int i = 0; i < texts.Count; i++)
            {
                result.Add(new LyricsLine
                {
                    time = totalSeconds * i / texts.Count,
                    text = texts[i]
                });
            }
            return result;
        }

        private static List<string> SplitPlainText(string text)
        {
            return (text ?? "")
                .Replace("\r", "")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        private async Task<List<LyricsLine>?> TryLrclibAsync(string name, string artists, long durationMs, CancellationToken ct)
        {
            string track = (name ?? "").Trim();
            string artist = (artists ?? "").Split('/').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            if (track.Length == 0) return null;

            string getUrl = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track);
            if (artist.Length > 0)
                getUrl += "&artist_name=" + Uri.EscapeDataString(artist);
            if (durationMs > 0)
                getUrl += "&duration=" + (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

            var exact = await GetJsonAsync(getUrl, ct);
            string? exactSynced = (exact as JObject)?["syncedLyrics"]?.ToString();
            if (!string.IsNullOrWhiteSpace(exactSynced))
            {
                ParsedLyrics parsed = ParseLrc(exactSynced);
                if (parsed.Lines.Count > 0) return parsed.Lines;
            }

            string query = track + (artist.Length > 0 ? " " + artist : "");
            string searchUrl = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query);
            var search = await GetJsonAsync(searchUrl, ct);
            if (search is not JArray items) return null;

            JObject? best = null;
            int bestScore = int.MinValue;
            double durationSeconds = durationMs > 0 ? durationMs / 1000.0 : 0;
            foreach (var token in items.OfType<JObject>())
            {
                string synced = token["syncedLyrics"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(synced)) continue;

                int score = 0;
                string candidateTrack = token["trackName"]?.ToString() ?? "";
                string candidateArtist = token["artistName"]?.ToString() ?? "";
                if (Normalize(candidateTrack).Contains(Normalize(track)) ||
                    Normalize(track).Contains(Normalize(candidateTrack))) score += 4;
                if (artist.Length > 0 && Normalize(candidateArtist).Contains(Normalize(artist))) score += 3;

                double candidateDuration = token["duration"]?.Value<double>() ?? 0;
                if (durationSeconds > 0 && candidateDuration > 0)
                {
                    double diff = Math.Abs(candidateDuration - durationSeconds);
                    if (diff <= 3) score += 4;
                    else if (diff <= 8) score += 2;
                    else score -= Math.Min(10, (int)diff);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = token;
                }
            }

            if (best == null || bestScore < 3) return null;
            ParsedLyrics bestParsed = ParseLrc(best["syncedLyrics"]?.ToString() ?? "");
            return bestParsed.Lines.Count > 0 ? bestParsed.Lines : null;
        }

        private static async Task<JToken?> GetJsonAsync(string url, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return null;
                string text = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<JObject?> FetchNeteasePublicAsync(string songId, CancellationToken ct)
        {
            try
            {
                string url = "https://music.163.com/api/song/lyric?id=" +
                    Uri.EscapeDataString(songId) + "&lv=-1&kv=-1&tv=-1";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return null;
                string text = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static string Normalize(string value)
        {
            return NormalizeRegex.Replace((value ?? "").ToLowerInvariant(), "");
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "BiliNCM-Deck/1.1.0 (+https://github.com/DDG-LAL/BiliNCM_Deck)");
            return client;
        }

        private static CloudMusicApi CreateApi()
        {
            var api = new CloudMusicApi();
            api.Cookies.Add(new Cookie("os", "pc", "/", ".music.163.com"));
            api.Cookies.Add(new Cookie("appver", "2.10.6", "/", ".music.163.com"));
            return api;
        }
    }

    internal sealed class LyricsPlayback
    {
        private readonly object _lock = new object();
        private string _songId = "";
        private double _lastPosition;
        private long _lastAt;

        public (bool playing, double position) Resolve(CompanionQueueEngine.SongView current, long nowMs)
        {
            lock (_lock)
            {
                string songId = current.id ?? "";
                bool playing = IsPlaying(current.state);
                double duration = current.duration > 0 ? current.duration / 1000.0 : 0;
                bool songChanged = !string.Equals(songId, _songId, StringComparison.Ordinal);

                if (songChanged)
                {
                    _songId = songId;
                    _lastPosition = 0;
                    _lastAt = nowMs;
                }

                double position;
                if (current.position.HasValue)
                {
                    position = NormalizePosition(current.position.Value, duration);
                }
                else if (playing && current.start > 0)
                {
                    position = (nowMs - current.start) / 1000.0;
                }
                else if (playing)
                {
                    position = _lastPosition + Math.Max(0, nowMs - _lastAt) / 1000.0;
                }
                else
                {
                    position = _lastPosition;
                }

                if (duration > 0)
                    position = Math.Clamp(position, 0, duration);
                else if (position < 0)
                    position = 0;

                _lastPosition = position;
                _lastAt = nowMs;
                return (playing, position);
            }
        }

        private static bool IsPlaying(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return true;
            string value = state.Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
                return numeric != 0 && numeric != 2;

            value = value.ToLowerInvariant();
            if (value is "1" or "true" or "playing" or "play" or "resume") return true;
            if (value is "0" or "2" or "false" or "paused" or "pause" or "stopped" or "stop") return false;
            return true;
        }

        private static double NormalizePosition(double position, double durationSeconds)
        {
            if (position < 0) return 0;
            if (durationSeconds > 0 && position > durationSeconds * 1.5 && position > 100)
                position /= 1000.0;
            else if (durationSeconds <= 0 && position > 3600)
                position /= 1000.0;
            return durationSeconds > 0 ? Math.Min(position, durationSeconds) : position;
        }
    }
}
