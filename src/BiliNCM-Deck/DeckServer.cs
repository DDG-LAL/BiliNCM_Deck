using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// 5566: OBS overlay + settings + local deck API.
    /// Serves the deck HTML/config/state and processes uploaded backgrounds
    /// with EXIF orientation, pixel limits and long-edge resizing.
    /// </summary>
    internal sealed class DeckServer : IDisposable
    {
        private const int Port = 5566;
        private const long MaxConfigBytes = 1024 * 1024;
        private const long MaxImageBytes = 12 * 1024 * 1024;
        private const long MaxImagePixels = 40000000;
        private const int MaxImageEdge = 3840;
        private static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private readonly HttpListener _listener = new HttpListener();
        private readonly string _baseDir;
        private readonly string _deckDir;
        private readonly string _assetsDir;
        private readonly string _configFile;
        private readonly string _lyricsConfigFile;
        private readonly object _configLock = new object();
        private readonly object _lyricsConfigLock = new object();
        private readonly LyricsService _lyrics = new LyricsService();
        private readonly LyricsPlayback _lyricsPlayback = new LyricsPlayback();
        private bool _started;

        private static readonly Regex AssetName = new Regex(
            @"^bg-[A-Za-z0-9-]+\.(png|jpg|jpeg)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly JObject DefaultConfig = JObject.Parse(@"{
  ""version"": 1,
  ""skin"": ""default"",
  ""background"": {
    ""file"": """",
    ""fit"": ""cover"",
    ""opacity"": 100,
    ""scrim"": 35,
    ""focusX"": 50,
    ""focusY"": 50
  }
}");

        public DeckServer()
        {
            _baseDir = AppContext.BaseDirectory;
            _deckDir = Path.Combine(_baseDir, "web", "deck");
            _assetsDir = Path.Combine(_baseDir, "overlay-assets");
            _configFile = Path.Combine(_baseDir, "overlay-config.json");
            _lyricsConfigFile = Path.Combine(_baseDir, "lyrics-config.json");
            Directory.CreateDirectory(_assetsDir);
        }

        public void Start()
        {
            if (_started) return;
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _started = true;
            Program.WriteLog($"DeckServer listening on http://127.0.0.1:{Port}/", ConsoleColor.Cyan);
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_started)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            try
            {
                AddCommonHeaders(res);
                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 200; return; }

                var path = req.Url?.AbsolutePath ?? "/";

                if (path == "/health")
                {
                    SendJson(res, new {
                        ok = true,
                        port = Port,
                        pid = Environment.ProcessId,
                        root = _baseDir,
                        version = Version
                    });
                    return;
                }

                if (path == "/state")
                {
                    SendJson(res, Program.BuildDeckState());
                    return;
                }

                if (path == "/api/ncm/state")
                {
                    var ncm = await NcmCdp.GetStateAsync(Program.CdpPort);
                    if (ncm == null)
                    {
                        SendError(res, 503, "ncm_unavailable");
                        return;
                    }
                    SendJson(res, ncm);
                    return;
                }

                if (path == "/api/lyrics/config")
                {
                    if (req.HttpMethod == "GET")
                    {
                        SendJson(res, new { ok = true, config = ReadLyricsConfig() });
                        return;
                    }

                    if (req.HttpMethod == "POST")
                    {
                        if (!IsLocalOrigin(req)) { SendError(res, 403, "origin_denied"); return; }
                        var body = await ReadBodyAsync(req, MaxConfigBytes);
                        if (body == null) { SendError(res, 413, "too_large"); return; }
                        LyricsConfig? incoming;
                        try { incoming = JsonConvert.DeserializeObject<LyricsConfig>(Encoding.UTF8.GetString(body)); }
                        catch { SendError(res, 400, "invalid_json"); return; }
                        var cfg = (incoming ?? new LyricsConfig()).Normalize();
                        WriteLyricsConfig(cfg);
                        SendJson(res, new { ok = true, config = cfg });
                        return;
                    }

                    SendError(res, 405, "method_not_allowed");
                    return;
                }

                if (path == "/api/lyrics/state")
                {
                    var cfg = ReadLyricsConfig();
                    var current = Program.GetCurrentSongView();
                    if (current == null || string.IsNullOrWhiteSpace(current.id))
                    {
                        SendJson(res, new
                        {
                            ok = true,
                            noSong = true,
                            playing = false,
                            position = 0.0,
                            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            noLyrics = true,
                            hasTimeline = false,
                            source = "none",
                            plainText = "",
                            lines = Array.Empty<LyricsLine>(),
                            config = cfg
                        });
                        return;
                    }

                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var (playing, position) = _lyricsPlayback.Resolve(current, now);
                    var lyrics = await _lyrics.GetAsync(current.id, current.name, current.artists, current.duration);
                    SendJson(res, new
                    {
                        ok = true,
                        noSong = false,
                        song = new
                        {
                            id = current.id,
                            name = current.name,
                            artists = current.artists,
                            duration = current.duration > 0 ? current.duration / 1000.0 : 0
                        },
                        playing,
                        position,
                        serverTime = now,
                        state = current.state,
                        start = current.start,
                        noLyrics = lyrics.noLyrics,
                        hasTimeline = lyrics.hasTimeline,
                        source = lyrics.source,
                        plainText = lyrics.plainText,
                        lines = lyrics.lines,
                        error = lyrics.error,
                        config = cfg
                    });
                    return;
                }

                if (path == "/api/overlay-config")
                {
                    if (req.HttpMethod == "GET")
                    {
                        var cfg = ReadConfig();
                        SendJson(res, new {
                            ok = true,
                            config = cfg,
                            fileExists = HasConfiguredAsset(cfg)
                        });
                        return;
                    }

                    if (req.HttpMethod == "POST")
                    {
                        if (!IsLocalOrigin(req)) { SendError(res, 403, "origin_denied"); return; }
                        var body = await ReadBodyAsync(req, MaxConfigBytes);
                        if (body == null) { SendError(res, 413, "too_large"); return; }
                        JObject incoming;
                        try { incoming = JObject.Parse(Encoding.UTF8.GetString(body)); }
                        catch { SendError(res, 400, "invalid_json"); return; }

                        lock (_configLock)
                        {
                            File.WriteAllText(_configFile, incoming.ToString(Formatting.Indented));
                        }
                        SendJson(res, new { ok = true, config = incoming });
                        return;
                    }

                    SendError(res, 405, "method_not_allowed");
                    return;
                }

                if (path == "/api/overlay-upload" && req.HttpMethod == "POST")
                {
                    if (!IsLocalOrigin(req)) { SendError(res, 403, "origin_denied"); return; }
                    var body = await ReadBodyAsync(req, MaxImageBytes);
                    if (body == null) { SendError(res, 413, "too_large"); return; }
                    if (!TrySaveOverlayImage(body, out var fileName, out var imageError, out var width, out var height))
                    {
                        var status = imageError is "too_large" or "too_large_pixels" ? 413
                            : imageError == "unsupported_type" ? 415
                            : 400;
                        SendError(res, status, imageError);
                        return;
                    }

                    var cfg = ReadConfig();
                    var bg = cfg["background"] as JObject;
                    if (bg == null)
                    {
                        bg = new JObject();
                        cfg["background"] = bg;
                    }
                    bg["file"] = fileName;
                    lock (_configLock)
                    {
                        File.WriteAllText(_configFile, cfg.ToString(Formatting.Indented));
                    }
                    SendJson(res, new { ok = true, config = cfg, file = fileName, width, height });
                    return;
                }

                if (path.StartsWith("/assets/", StringComparison.Ordinal))
                {
                    var name = Uri.UnescapeDataString(path.Substring("/assets/".Length));
                    if (!AssetName.IsMatch(name))
                    {
                        SendError(res, 404, "not_found");
                        return;
                    }
                    var file = Path.Combine(_assetsDir, name);
                    if (!File.Exists(file))
                    {
                        SendError(res, 404, "not_found");
                        return;
                    }
                    SendBytes(res, File.ReadAllBytes(file), ContentTypeFor(name));
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    SendFile(res, Path.Combine(_deckDir, "BiliNCM-Overlay.html"), "text/html; charset=utf-8");
                    return;
                }

                if (path == "/settings" || path == "/settings.html")
                {
                    SendFile(res, Path.Combine(_deckDir, "BiliNCM-Overlay-Settings.html"), "text/html; charset=utf-8");
                    return;
                }

                if (path == "/lyrics" || path == "/lyrics/")
                {
                    SendFile(res, Path.Combine(_deckDir, "BiliNCM-Lyrics.html"), "text/html; charset=utf-8");
                    return;
                }

                if (path == "/favicon.ico")
                {
                    res.StatusCode = 204;
                    return;
                }

                // All former 5555 bot APIs now live on 5566.
                if (await Program.TryHandleLocalApiAsync(context)) return;

                SendError(res, 404, "not_found");
            }
            catch (Exception ex)
            {
                Program.WriteLog($"request failed: {req.HttpMethod} {req.Url?.AbsolutePath}: {ex}", ConsoleColor.Red);
                try { SendError(res, 500, "server_error"); } catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        private JObject ReadConfig()
        {
            lock (_configLock)
            {
                try
                {
                    if (File.Exists(_configFile))
                    {
                        var parsed = JObject.Parse(File.ReadAllText(_configFile));
                        if (parsed["background"] == null) parsed["background"] = DefaultConfig["background"]?.DeepClone();
                        return parsed;
                    }
                }
                catch { }
                return (JObject)DefaultConfig.DeepClone();
            }
        }

        private bool HasConfiguredAsset(JObject cfg)
        {
            try
            {
                var file = (string?)cfg["background"]?["file"];
                if (string.IsNullOrWhiteSpace(file) || !AssetName.IsMatch(file)) return false;
                return File.Exists(Path.Combine(_assetsDir, file));
            }
            catch { return false; }
        }

        private LyricsConfig ReadLyricsConfig()
        {
            lock (_lyricsConfigLock)
            {
                try
                {
                    if (File.Exists(_lyricsConfigFile))
                    {
                        var parsed = JsonConvert.DeserializeObject<LyricsConfig>(File.ReadAllText(_lyricsConfigFile));
                        return (parsed ?? new LyricsConfig()).Normalize();
                    }
                }
                catch { }
                return new LyricsConfig();
            }
        }

        private void WriteLyricsConfig(LyricsConfig config)
        {
            lock (_lyricsConfigLock)
            {
                File.WriteAllText(_lyricsConfigFile, JsonConvert.SerializeObject(config.Normalize(), Formatting.Indented));
            }
        }

        internal static bool IsLocalOrigin(HttpListenerRequest req)
        {
            var origin = req.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin)) return true;
            return origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
        }

        private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest req, long maxBytes)
        {
            if (req.ContentLength64 > maxBytes) return null;
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0;
            int read;
            while ((read = await req.InputStream.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) return null;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }

        private bool TrySaveOverlayImage(byte[] bytes, out string fileName, out string error, out int width, out int height)
        {
            fileName = "";
            error = "";
            width = 0;
            height = 0;
            if (bytes.Length == 0) { error = "empty_file"; return false; }
            if (bytes.Length > MaxImageBytes) { error = "too_large"; return false; }

            try
            {
                using var input = new MemoryStream(bytes);
                using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
                if (source.Width < 1 || source.Height < 1) { error = "decode_failed"; return false; }
                if ((long)source.Width * source.Height > MaxImagePixels) { error = "too_large_pixels"; return false; }

                bool isPng = source.RawFormat.Guid == ImageFormat.Png.Guid;
                bool isJpeg = source.RawFormat.Guid == ImageFormat.Jpeg.Guid;
                if (!isPng && !isJpeg) { error = "unsupported_type"; return false; }

                int orientation = 1;
                try
                {
                    var prop = source.GetPropertyItem(0x0112);
                    if (prop?.Value != null && prop.Value.Length >= 2)
                        orientation = BitConverter.ToUInt16(prop.Value, 0);
                }
                catch { }

                switch (orientation)
                {
                    case 2: source.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: source.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: source.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                    case 5: source.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: source.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: source.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: source.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }

                int srcW = source.Width;
                int srcH = source.Height;
                double scale = 1.0;
                int longEdge = Math.Max(srcW, srcH);
                if (longEdge > MaxImageEdge) scale = (double)MaxImageEdge / longEdge;
                width = Math.Max(1, (int)Math.Round(srcW * scale));
                height = Math.Max(1, (int)Math.Round(srcH * scale));

                var pixelFormat = isPng ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb;
                using var resized = new Bitmap(width, height, pixelFormat);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.Clear(isPng ? Color.Transparent : Color.White);
                    graphics.DrawImage(source, 0, 0, width, height);
                }

                byte[] output;
                string extension;
                using (var outputStream = new MemoryStream())
                {
                    if (isPng)
                    {
                        resized.Save(outputStream, ImageFormat.Png);
                        extension = "png";
                    }
                    else
                    {
                        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
                        if (codec == null) { error = "encoder_missing"; return false; }
                        using var encoderParams = new EncoderParameters(1);
                        using var quality = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                        encoderParams.Param[0] = quality;
                        resized.Save(outputStream, codec, encoderParams);
                        extension = "jpg";
                    }
                    output = outputStream.ToArray();
                }

                Directory.CreateDirectory(_assetsDir);
                fileName = "bg-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 8) + "." + extension;
                File.WriteAllBytes(Path.Combine(_assetsDir, fileName), output);
                return true;
            }
            catch
            {
                error = "decode_failed";
                return false;
            }
        }

        private static string ContentTypeFor(string name)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".png" ? "image/png" : "image/jpeg";
        }

        private static void AddCommonHeaders(HttpListenerResponse res)
        {
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "*";
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
        }

        private static void SendFile(HttpListenerResponse res, string path, string contentType)
        {
            if (!File.Exists(path)) { SendError(res, 404, "not_found"); return; }
            SendBytes(res, File.ReadAllBytes(path), contentType);
        }

        private static void SendBytes(HttpListenerResponse res, byte[] bytes, string contentType)
        {
            res.StatusCode = 200;
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private static void SendJson(HttpListenerResponse res, object obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            SendBytes(res, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");
        }

        private static void SendError(HttpListenerResponse res, int status, string code)
        {
            res.StatusCode = status;
            var json = JsonConvert.SerializeObject(new { ok = false, error = code });
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            _started = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
