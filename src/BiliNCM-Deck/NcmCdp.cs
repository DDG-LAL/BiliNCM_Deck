using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// Shared NCM CDP bridge. The JS expressions are the tested expressions
    /// extracted from BiliNCM-Companion (web/ncm/*.js).
    /// </summary>
    internal static class NcmCdp
    {
        public static string? LoadScript(string fileName)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "web", "ncm", fileName);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        public static async Task<JObject?> GetStateAsync(int port, int timeoutMs = 10000)
        {
            var script = LoadScript("state.js");
            if (string.IsNullOrWhiteSpace(script)) return null;
            var raw = await EvaluateAsync(script, port, timeoutMs, false);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); } catch { return null; }
        }

        public static async Task<JObject?> AddSongAsync(int port, string songId, int offset, string mode, IEnumerable<string>? pendingQids = null)
        {
            if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit)) return null;
            var template = LoadScript("add.js");
            if (string.IsNullOrWhiteSpace(template)) return null;
            var offsetMode = GetOffsetMode(offset, mode);
            var pending = ToJsStringArray(pendingQids);
            var expression = template
                .Replace("__SONG_ID__", songId)
                .Replace("__OFFSET_MODE__", offsetMode)
                .Replace("__PENDING_QIDS__", pending)
                .Replace("__OFFSET__", offset < 0 ? "0" : offset.ToString());
            var raw = await EvaluateAsync(expression, port, 35000, true);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); } catch { return null; }
        }

        public static async Task<JObject?> MoveQueueItemAsync(int port, string songId, int offset, string mode, IEnumerable<string>? pendingQids = null)
        {
            if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit)) return null;
            var template = LoadScript("move.js");
            if (string.IsNullOrWhiteSpace(template)) return null;
            var offsetMode = GetOffsetMode(offset, mode);
            var pending = ToJsStringArray(pendingQids);
            var expression = template
                .Replace("__SONG_ID__", songId)
                .Replace("__OFFSET_MODE__", offsetMode)
                .Replace("__PENDING_QIDS__", pending)
                .Replace("__OFFSET__", offset < 0 ? "0" : offset.ToString());
            var raw = await EvaluateAsync(expression, port, 25000, true);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); } catch { return null; }
        }

        public static async Task<JObject?> RemoveSongAsync(int port, string songId)
        {
            if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit)) return null;
            var template = LoadScript("remove.js");
            if (string.IsNullOrWhiteSpace(template)) return null;
            var expression = template.Replace("__SONG_ID__", songId);
            var raw = await EvaluateAsync(expression, port, 25000, true);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); } catch { return null; }
        }

        public static async Task<List<string>> GetTargetsAsync(int port, int timeoutMs = 3000)
        {
            var result = new List<string>();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                var text = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                var arr = JArray.Parse(text);
                foreach (var t in arr)
                {
                    if ((string?)t["type"] != "page") continue;
                    var url = (string?)t["url"] ?? "";
                    var ws = (string?)t["webSocketDebuggerUrl"] ?? "";
                    if (ws.Length == 0) continue;
                    if (url.Contains("orpheus", StringComparison.OrdinalIgnoreCase))
                        result.Insert(0, ws);
                    else
                        result.Add(ws);
                }
            }
            catch { }
            return result;
        }

        public static async Task<string?> EvaluateAsync(string expression, int port, int timeoutMs, bool awaitPromise)
        {
            var targets = await GetTargetsAsync(port);
            foreach (var wsUrl in targets)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    using var cts = new CancellationTokenSource(timeoutMs);
                    await ws.ConnectAsync(new Uri(wsUrl.Replace("ws://localhost:", "ws://127.0.0.1:")), cts.Token);

                    var payload = new
                    {
                        id = 1,
                        method = "Runtime.evaluate",
                        @params = new Dictionary<string, object>
                        {
                            { "expression", expression },
                            { "returnByValue", true },
                            { "awaitPromise", awaitPromise }
                        }
                    };
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                    using var ms = new MemoryStream();
                    var buffer = new byte[65536];
                    while (true)
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                                throw new InvalidOperationException("CDP connection closed");
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        JObject message;
                        try { message = JObject.Parse(text); } catch { continue; }
                        if ((int?)message["id"] != 1) continue;

                        var exception = message["result"]?["exceptionDetails"];
                        if (exception != null)
                            throw new InvalidOperationException("CDP JS exception: " + exception.ToString(Formatting.None));

                        var value = message["result"]?["result"]?["value"];
                        if (value == null) return null;
                        return value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
                    }
                }
                catch
                {
                    // Try the next page target.
                }
            }
            return null;
        }

        private static string GetOffsetMode(int offset, string mode)
        {
            if (mode is "pending" or "append" or "omit" or "explicit") return mode;
            if (offset == -2) return "omit";
            if (offset < 0) return "append";
            return "explicit";
        }

        private static string ToJsStringArray(IEnumerable<string>? items)
        {
            if (items == null) return "[]";
            var safe = items.Where(x => !string.IsNullOrWhiteSpace(x) && x.All(c => char.IsDigit(c) || c == '_'))
                            .Distinct()
                            .Take(200)
                            .Select(x => "\"" + x + "\"");
            return "[" + string.Join(",", safe) + "]";
        }
    }
}
