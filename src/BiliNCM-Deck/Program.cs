using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Newtonsoft.Json.Linq;
using NeteaseCloudMusicApi;
using System.Text.Json;

namespace BiliNetEaseIntegratedApp
{
    class Program
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        public class PermissionConfig {
            public bool AllowManager { get; set; } = true;    
            public int MinGuardType { get; set; } = 0;        
            public int MinMedalLevel { get; set; } = 0;       
            public string MedalName { get; set; } = "";       
        }

        public class AppConfig {
            public string BiliCookie { get; set; } = "";
            public long BiliUid { get; set; } = 0;
            public long RoomId { get; set; } = 0;
            
            public string AuthJson { get; set; } = "";
            public int CooldownMinutes { get; set; } = 0;
            public int AltHoldMs { get; set; } = 0; 
            public bool ShowDebugLogs { get; set; } = false;
            public bool ShowAllDanmaku { get; set; } = false;

            public bool EnableCDP { get; set; } = true;
            public int CdpPort { get; set; } = 9222;

            public bool EnableHUD { get; set; } = true; 
            public int RestoreDelayMs { get; set; } = 800; 
            public int MonitorIntervalMs { get; set; } = 300; 
            public List<string> SuperUsers { get; set; } = new List<string>(); 
            
            public PermissionConfig OrderPermission { get; set; } = new PermissionConfig { MinMedalLevel = 0 };
            // Legacy config field kept for JSON compatibility. Skip is intentionally
            // hard-limited to the streamer and room admins (IsStreamerOrModerator).
            public PermissionConfig SkipPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 0 };
            public PermissionConfig PriorityPermission { get; set; } = new PermissionConfig { AllowManager = true, MinGuardType = -1 };
            public PermissionConfig CancelPermission { get; set; } = new PermissionConfig { MinMedalLevel = 0 };
            
            public PermissionConfig ToggleAcceptPermission { get; set; } = new PermissionConfig { AllowManager = true, MinGuardType = -1 };
            public PermissionConfig ForceControlPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 0, MinGuardType = -1 };
            
            public JsonElement? OverlayUIConfig { get; set; }
        }

        public struct UserInfo {
            public string Name { get; set; }
            public string Uid { get; set; }
            public bool IsManager { get; set; }
            public int GuardType { get; set; }
            public int MedalLevel { get; set; }
            public string MedalName { get; set; }
        }

        public struct SongInfo {
            public string Id { get; set; }
            public string SongName { get; set; }
            public string ArtistName { get; set; }
            public string OrderedBy { get; set; }
            public string OrderedByUid { get; set; }
            public string OrderedByAvatar { get; set; }
            public bool Priority { get; set; }
            public int PrioritySeq { get; set; }
            public string FullTitle => $"{ArtistName} - {SongName}";
        }

        public struct LogEntry {
            public string Time { get; set; }
            public string Message { get; set; }
            public string Color { get; set; }
        }

        static AppConfig _config = new AppConfig();
        static CloudMusicApi _neteaseApi = new CloudMusicApi();
        static List<SongInfo> _targetQueue = new List<SongInfo>(); 
        static readonly object _queueLock = new object();
        
        static ConcurrentDictionary<string, DateTime> _userCooldowns = new ConcurrentDictionary<string, DateTime>();
        static ConcurrentDictionary<string, string> _avatarCache = new ConcurrentDictionary<string, string>();
        
        static SongInfo? _currentPlayingSong = null; 
        
        static bool _acceptingRequests = true; 
        static bool _isPlayingEnabled = true;

        
        static CancellationTokenSource _biliCts = new CancellationTokenSource();
        
        static string _qrCodeBase64 = "";
        static string _qrLoginStatus = "等待获取二维码...";
        static bool _isQrLoggingIn = false;

        static class OverlayState {
            public static string Status = "等待中...";
            public static SongInfo? CurrentPlaying = null;
            public static List<SongInfo> Queue = new List<SongInfo>();
            public static string LastToastMsg = "";
            public static long LastToastTime = 0;
        }
        static DeckServer _deckServer;
        static bool _noBrowser = false;
        static string _legacyRootArg = "";
        static string _lastRoomError = "";
        static int _liveStatus = -1;
        static string _liveStatusSource = "";
        static DateTime _liveStatusAt = DateTime.MinValue;
        static CancellationTokenSource _liveStatusCts;
        static int _topSeq;
        static CompanionQueueEngine _companionEngine;
        static TrayHost _tray;
        static readonly CancellationTokenSource _engineCts = new CancellationTokenSource();
        static int _shutdownRequested;

        internal static int CdpPort => _config.CdpPort;

        static List<LogEntry> _sysLogs = new List<LogEntry>();
        static readonly object _consoleLock = new object();

        static void MarkQueueDirty() { }

        [STAThread] 
        static void Main(string[] args)
        {
            _noBrowser = args.Any(a => string.Equals(a, "--no-browser", StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < args.Length; i++) {
                if (string.Equals(args[i], "--legacy-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    _legacyRootArg = args[++i];
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                var text = $"发生未捕获的严重错误：\n{e.ExceptionObject}";
                try { WriteLog("[崩溃] " + text, ConsoleColor.Red); } catch { }
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), text); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) => {
                try { WriteLog("[崩溃] 未观察的任务异常: " + e.Exception, ConsoleColor.Red); } catch { }
                e.SetObserved();
            };

            using (Mutex mutex = new Mutex(true, "Global\\BiliNCM_Deck_Mutex_Unique", out bool createdNew))
            {
                if (!createdNew)
                {
                    LaunchDeckFrontend();
                    return;
                }

                var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                WriteLog($">>> [系统] BiliNCM Deck {version} 启动 PID={Environment.ProcessId} 根目录={AppContext.BaseDirectory}", ConsoleColor.Green);
                WriteLog($">>> [系统] 启动参数: {(args.Length > 0 ? string.Join(" ", args) : "(none)")}", ConsoleColor.Gray);

                var handle = GetConsoleWindow();
                ShowWindow(handle, SW_HIDE);

                if (!LoadConfig()) {
                    TryMigrateLegacyConfig(_legacyRootArg);
                    if (!LoadConfig()) SaveConfig();
                }
                EnforceRestrictedCommandPermissions();
                WriteLog($">>> [配置] 房间={_config.RoomId} UID={_config.BiliUid} 登录={(!string.IsNullOrEmpty(_config.BiliCookie)).ToString().ToLowerInvariant()} CDP={_config.CdpPort}", ConsoleColor.Gray);
                if (_config.ToggleAcceptPermission.MinGuardType == 0 && _config.ToggleAcceptPermission.MinMedalLevel == 0) {
                    WriteLog("[配置] 警告: 开启/关闭点歌对所有观众开放；建议在 config.json 收紧 ToggleAcceptPermission", ConsoleColor.Yellow);
                }

                // Deck owns the queue. Keep the bot's own auto-play/radar
                // correction from fighting the NCM playlist.
                _isPlayingEnabled = false;

                try {
                    _deckServer = new DeckServer();
                    _deckServer.Start();
                } catch (Exception ex) {
                    WriteLog($"[Deck] 5566 服务启动失败: {ex.Message}", ConsoleColor.Red);
                }
                SyncHUD("后端 API 已就绪");
                WriteLog(">>> [系统] 点歌机核心服务启动成功！", ConsoleColor.Green);

                _neteaseApi.Cookies.Add(new Cookie("os", "pc", "/", ".music.163.com"));
                _neteaseApi.Cookies.Add(new Cookie("appver", "2.10.6", "/", ".music.163.com"));

                EnsureNcmCdp();

                _companionEngine = new CompanionQueueEngine(
                    CaptureBotSnapshot,
                    () => _config.CdpPort,
                    SetDeckCurrent,
                    (message, color) => WriteLog(message, color),
                    PauseBotAutoPlay);
                _ = Task.Run(() => _companionEngine.RunAsync(_engineCts.Token));

                if (_config.RoomId > 0 && !string.IsNullOrEmpty(_config.BiliCookie)) {
                    WriteLog($">>> [系统] 检测到历史房间配置，自动尝试连接直播间: {_config.RoomId}", ConsoleColor.Yellow);
                    _ = Task.Run(() => ConnectToLiveRoom(_config.RoomId));
                }

                try {
                    _tray = new TrayHost(
                        () => OpenBrowser("http://127.0.0.1:5566/"),
                        () => OpenBrowser("http://127.0.0.1:5566/settings"),
                        OpenLogDirectory,
                        () => RequestShutdown("tray"));
                    _tray.Start();
                    WriteLog(">>> [系统] 托盘图标已启动；关闭页面不会退出程序", ConsoleColor.Gray);
                } catch (Exception ex) {
                    WriteLog("[托盘] 启动失败: " + ex.Message, ConsoleColor.Yellow);
                }

                LaunchDeckFrontend();
                Thread.Sleep(Timeout.Infinite);
            }
        }

        static void LaunchDeckFrontend()
        {
            if (_noBrowser) return;
            OpenBrowser("http://127.0.0.1:5566/");
            Thread.Sleep(300);
            OpenBrowser("http://127.0.0.1:5566/settings");
        }

        static void OpenBrowser(string url)
        {
            try {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            } catch (Exception ex) {
                WriteLog("[浏览器] 打开页面失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        static void OpenLogDirectory()
        {
            try {
                Process.Start(new ProcessStartInfo { FileName = AppContext.BaseDirectory, UseShellExecute = true });
            } catch (Exception ex) {
                WriteLog("[日志] 打开日志目录失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        internal static void RequestShutdown(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
            WriteLog($">>> [系统] 正在退出 ({reason})...", ConsoleColor.Yellow);
            try { _biliCts.Cancel(); } catch { }
            try { _liveStatusCts?.Cancel(); } catch { }
            try { _engineCts.Cancel(); } catch { }
            try { _deckServer?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            WriteLog(">>> [系统] 已退出", ConsoleColor.Gray);
            Environment.Exit(0);
        }

        internal static async Task<bool> TryHandleLocalApiAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            try {
                string urlPath = req.Url?.AbsolutePath ?? "";
                if (!DeckServer.IsLocalOrigin(req)) {
                    var origin = req.Headers["Origin"];
                    WriteLog($"[API] 拒绝非本地 Origin: {req.HttpMethod} {urlPath} origin={origin}", ConsoleColor.Yellow);
                    return WriteApiJson(res, "{\"success\":false,\"error\":\"origin_denied\"}", 403);
                }

                if (urlPath == "/api/sys/restart_ncm" && req.HttpMethod == "POST") {
                    EnsureNcmCdp();
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/sys/open_admin" && req.HttpMethod == "POST") {
                    Process.Start(new ProcessStartInfo { FileName = "http://127.0.0.1:5566/settings", UseShellExecute = true });
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/exit" && req.HttpMethod == "POST") {
                    WriteApiJson(res, "{\"success\":true}");
                    _ = Task.Run(async () => {
                        await Task.Delay(150);
                        RequestShutdown("api");
                    });
                    return true;
                }

                if (urlPath == "/api/state/toggle" && req.HttpMethod == "POST") {
                    _acceptingRequests = !_acceptingRequests;
                    SyncHUD();
                    WriteLog($">>> [系统] 接收点歌功能目前已 {(_acceptingRequests ? "开启" : "暂停")}", _acceptingRequests ? ConsoleColor.Green : ConsoleColor.Yellow);
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/state/toggle_play" && req.HttpMethod == "POST") {
                    _isPlayingEnabled = !_isPlayingEnabled;
                    SyncHUD();
                    WriteLog($">>> [系统] 自动播放标记目前已 {(_isPlayingEnabled ? "开启" : "关闭")}", _isPlayingEnabled ? ConsoleColor.Green : ConsoleColor.Yellow);
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/queue/action" && req.HttpMethod == "POST") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var doc = JsonDocument.Parse(reader.ReadToEnd());
                    string action = doc.RootElement.GetProperty("action").GetString();
                    if (action == "play_now" || action == "push_current_to_queue" || action == "skip_current") {
                        return WriteApiJson(res, "{\"success\":false,\"error\":\"force_action_removed\"}", 400);
                    }

                    lock (_queueLock) {
                        if (action == "reorder") {
                            int from = doc.RootElement.GetProperty("from").GetInt32();
                            int to = doc.RootElement.GetProperty("to").GetInt32();
                            if (from >= 0 && from < _targetQueue.Count && to >= 0 && to < _targetQueue.Count) {
                                var item = _targetQueue[from];
                                _targetQueue.RemoveAt(from);
                                _targetQueue.Insert(to, item);
                            }
                        } else if (doc.RootElement.TryGetProperty("index", out var idxProp)) {
                            int index = idxProp.GetInt32();
                            if (index >= 0 && index < _targetQueue.Count) {
                                if (action == "delete") {
                                    var target = _targetQueue[index];
                                    _targetQueue.RemoveAt(index);
                                    bool keepCopy = HasAnotherPendingSong(target.Id);
                                    bool isCurrent = IsCurrentSongId(target.Id);
                                    if (keepCopy) WriteLog($"[移除] 仍有其他点歌，保留 NCM 副本: {target.SongName}", ConsoleColor.Gray);
                                    else if (isCurrent) WriteLog($"[移除] 目标是当前歌，保留 NCM 副本: {target.SongName}", ConsoleColor.Gray);
                                    else _ = RemovePendingSongAsync(target.Id);
                                } else if (action == "top") {
                                    var item = _targetQueue[index];
                                    _targetQueue.RemoveAt(index);
                                    _targetQueue.Insert(0, item);
                                    int prioritySeq = Interlocked.Increment(ref _topSeq);
                                    _companionEngine?.Prioritize(item.Id, prioritySeq);
                                }
                            }
                        }
                    }
                    MarkQueueDirty();
                    SyncHUD();
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/logs" && req.HttpMethod == "GET") {
                    List<LogEntry> currentLogs;
                    lock (_consoleLock) { currentLogs = new List<LogEntry>(_sysLogs); }
                    return WriteApiJson(res, JsonSerializer.Serialize(currentLogs));
                }

                if (urlPath == "/api/room" && req.HttpMethod == "POST") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var doc = JsonDocument.Parse(reader.ReadToEnd());
                    long newRoomId = doc.RootElement.GetProperty("roomId").GetInt64();
                    bool success = await ConnectToLiveRoom(newRoomId);
                    var result = new {
                        success,
                        error = success ? null : (_lastRoomError.Length > 0 ? _lastRoomError : "room_connect_failed")
                    };
                    return WriteApiJson(res, JsonSerializer.Serialize(result));
                }

                if (urlPath == "/api/update/check" && req.HttpMethod == "GET") {
                    return WriteApiJson(res, "{\"hasUpdate\":false,\"error\":\"update_disabled\"}");
                }

                if (urlPath == "/api/update/apply" && req.HttpMethod == "POST") {
                    return WriteApiJson(res, "{\"status\":\"disabled\"}");
                }

                if (urlPath == "/api/config") {
                    if (req.HttpMethod == "GET") {
                        var safeConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new AppConfig();
                        safeConfig.BiliCookie = "";
                        safeConfig.AuthJson = "";
                        var wrap = new {
                            config = safeConfig,
                            roomId = _config.RoomId,
                            accepting = _acceptingRequests,
                            playing = _isPlayingEnabled,
                            biliLogin = !string.IsNullOrEmpty(_config.BiliCookie),
                            uid = _config.BiliUid,
                            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
                        };
                        return WriteApiJson(res, JsonSerializer.Serialize(wrap));
                    }
                    if (req.HttpMethod == "POST") {
                        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                        var newConf = JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd());
                        if (newConf != null) {
                            newConf.BiliCookie = _config.BiliCookie;
                            newConf.BiliUid = _config.BiliUid;
                            newConf.RoomId = _config.RoomId;
                            newConf.AuthJson = _config.AuthJson;
                            _config = newConf;
                            SaveConfig();
                        }
                        return WriteApiJson(res, "{\"success\":true}");
                    }
                    return WriteApiJson(res, "{\"success\":false,\"error\":\"method_not_allowed\"}", 405);
                }

                if (urlPath == "/api/bili/qrstart" && req.HttpMethod == "POST") {
                    if (!_isQrLoggingIn) { _ = Task.Run(() => BiliQrLoginAsync()); }
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/api/bili/qrstatus" && req.HttpMethod == "GET") {
                    var obj = new { qrBase64 = _qrCodeBase64, status = _qrLoginStatus, isLogin = !string.IsNullOrEmpty(_config.BiliCookie) };
                    return WriteApiJson(res, JsonSerializer.Serialize(obj));
                }

                if (req.HttpMethod == "POST" && urlPath == "/api/uiconfig") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    _config.OverlayUIConfig = JsonSerializer.Deserialize<JsonElement>(reader.ReadToEnd());
                    SaveConfig();
                    return WriteApiJson(res, "{\"success\":true}");
                }

                if (urlPath == "/data") {
                    var obj = new {
                        status = OverlayState.Status,
                        current = OverlayState.CurrentPlaying,
                        queue = OverlayState.Queue,
                        uiConfig = _config.OverlayUIConfig,
                        toast = new { msg = OverlayState.LastToastMsg, time = OverlayState.LastToastTime },
                        accepting = _acceptingRequests,
                        playing = _isPlayingEnabled
                    };
                    return WriteApiJson(res, JsonSerializer.Serialize(obj));
                }

                return false;
            } catch (Exception ex) {
                try {
                    WriteLog($"[API] {req.Url?.AbsolutePath} 处理失败: {ex.Message}", ConsoleColor.Red);
                    return WriteApiJson(res, "{\"success\":false,\"error\":\"server_error\"}", 500);
                } catch { return true; }
            }
        }

        static bool WriteApiJson(HttpListenerResponse res, string json, int status = 200)
        {
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            return true;
        }

        internal static CompanionQueueEngine.SongView? GetCurrentSongView()
        {
            try { return _companionEngine?.Snapshot().current; }
            catch { return null; }
        }

        internal static object BuildDeckState()
        {
            var engine = _companionEngine;
            if (engine != null) {
                var s = engine.Snapshot();
                return new {
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    bot = s.bot,
                    ncm = s.ncm,
                    accepting = s.accepting,
                    botAutoPlay = s.botAutoPlay,
                    status = string.IsNullOrWhiteSpace(OverlayState.Status) ? "NCM 播放列表" : OverlayState.Status,
                    alert = s.alert,
                    current = s.current,
                    pending = s.pending,
                    pendingCount = s.pendingCount,
                    liveStatus = _liveStatus,
                    liveStatusSource = _liveStatusSource,
                    liveStatusUpdated = _liveStatusAt == DateTime.MinValue ? "" : _liveStatusAt.ToString("HH:mm:ss"),
                    updated = DateTime.Now.ToString("HH:mm:ss")
                };
            }
            return BuildBotDeckState();
        }

        static object BuildBotDeckState()
        {
            object current = null;
            List<object> pending;
            lock (_queueLock) {
                if (_currentPlayingSong.HasValue) {
                    var c = _currentPlayingSong.Value;
                    current = new {
                        id = c.Id,
                        name = c.SongName,
                        artists = c.ArtistName,
                        orderer = c.OrderedBy
                    };
                }
                pending = _targetQueue.Select(s => (object)new {
                    id = s.Id,
                    name = s.SongName,
                    artists = s.ArtistName,
                    orderer = s.OrderedBy
                }).ToList();
            }
            return new {
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                bot = true,
                ncm = false,
                accepting = _acceptingRequests,
                botAutoPlay = _isPlayingEnabled,
                status = OverlayState.Status,
                alert = "",
                current,
                pending,
                pendingCount = pending.Count,
                liveStatus = _liveStatus,
                liveStatusSource = _liveStatusSource,
                liveStatusUpdated = _liveStatusAt == DateTime.MinValue ? "" : _liveStatusAt.ToString("HH:mm:ss"),
                updated = DateTime.Now.ToString("HH:mm:ss")
            };
        }

        static CompanionQueueEngine.BotSnapshot CaptureBotSnapshot()
        {
            lock (_queueLock) {
                return new CompanionQueueEngine.BotSnapshot {
                    Queue = new List<SongInfo>(_targetQueue),
                    Current = _currentPlayingSong,
                    Playing = _isPlayingEnabled,
                    Accepting = _acceptingRequests
                };
            }
        }

        static void SetDeckCurrent(SongInfo? current)
        {
            lock (_queueLock) { _currentPlayingSong = current; }
        }

        static void PauseBotAutoPlay()
        {
            bool shouldSkip = false;
            lock (_queueLock) {
                if (!_isPlayingEnabled) return;
                _isPlayingEnabled = false;
                if (_currentPlayingSong.HasValue) {
                    _targetQueue.Insert(0, _currentPlayingSong.Value);
                    _currentPlayingSong = null;
                    shouldSkip = true;
                }
            }
            if (shouldSkip) _ = TrySkipSongAsync();
            SyncHUD();
        }

        static bool TryMigrateLegacyConfig(string explicitRoot)
        {
            try {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var targetConfig = Path.Combine(baseDir, "config.json");
                if (File.Exists(targetConfig)) return false;

                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(explicitRoot)) candidates.Add(Path.GetFullPath(explicitRoot));
                candidates.Add(baseDir);

                foreach (var root in candidates) {
                    var legacyConfig = Path.Combine(root, "current", "config.json");
                    if (!File.Exists(legacyConfig)) continue;

                    var text = File.ReadAllText(legacyConfig);
                    try { JsonSerializer.Deserialize<AppConfig>(text); } catch { continue; }
                    File.WriteAllText(targetConfig, text);

                    CopyLegacyFileIfMissing(root, baseDir, "overlay-config.json");
                    CopyLegacyFileIfMissing(root, baseDir, "ncm-path.txt");
                    CopyLegacyDirectoryIfEmpty(root, baseDir, "overlay-assets");

                    WriteLog($">>> [迁移] 已从旧版 portable 导入配置: {root}", ConsoleColor.Green);
                    return true;
                }
            } catch (Exception ex) {
                WriteLog($"[迁移] 旧配置导入失败: {ex.Message}", ConsoleColor.Yellow);
            }
            return false;
        }

        static void CopyLegacyFileIfMissing(string sourceRoot, string targetRoot, string name)
        {
            try {
                var source = Path.Combine(sourceRoot, name);
                var target = Path.Combine(targetRoot, name);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            } catch { }
        }

        static void CopyLegacyDirectoryIfEmpty(string sourceRoot, string targetRoot, string name)
        {
            try {
                var source = Path.Combine(sourceRoot, name);
                var target = Path.Combine(targetRoot, name);
                if (!Directory.Exists(source)) return;
                Directory.CreateDirectory(target);
                if (Directory.EnumerateFileSystemEntries(target).Any()) return;
                foreach (var file in Directory.EnumerateFiles(source)) {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
            } catch { }
        }

        static void SyncHUD(string status = null) {
            if (!_config.EnableHUD) return;
            if (status != null) OverlayState.Status = status;
            OverlayState.CurrentPlaying = _currentPlayingSong;
            lock(_queueLock) { OverlayState.Queue = new List<SongInfo>(_targetQueue); }
        }

        internal static void WriteLog(string message, ConsoleColor color = ConsoleColor.Gray) {
            var safe = DeckLog.Redact(message);
            var level = color switch {
                ConsoleColor.Red => "ERROR",
                ConsoleColor.Yellow => "WARN",
                ConsoleColor.DarkGray => "DEBUG",
                _ => "INFO"
            };
            DeckLog.Append(level, safe);
            lock (_consoleLock) {
                Console.ForegroundColor = color;
                Console.WriteLine(safe);
                Console.ResetColor();
                _sysLogs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = safe, Color = color.ToString() });
                while (_sysLogs.Count > 1000) _sysLogs.RemoveAt(0);
            }
        }

        static string ResolveNcmExe() {
            var candidates = new List<string>();
            try {
                var pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ncm-path.txt");
                if (File.Exists(pathFile)) {
                    foreach (var raw in File.ReadAllLines(pathFile)) {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        line = line.Trim('"').Trim('\'');
                        if (!Path.IsPathRooted(line)) line = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, line);
                        candidates.Add(line);
                        break;
                    }
                }
            } catch { }

            foreach (var p in Process.GetProcessesByName("cloudmusic")) {
                try { if (!string.IsNullOrWhiteSpace(p.MainModule?.FileName)) candidates.Add(p.MainModule.FileName); } catch { }
            }
            try {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "Netease", "CloudMusic", "cloudmusic.exe"));
            } catch { }
            try {
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(pf86)) candidates.Add(Path.Combine(pf86, "Netease", "CloudMusic", "cloudmusic.exe"));
            } catch { }
            try {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(pf)) candidates.Add(Path.Combine(pf, "Netease", "CloudMusic", "cloudmusic.exe"));
            } catch { }

            foreach (var candidate in candidates) {
                try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
            }
            return "";
        }

        static bool HasNcmCdpTarget() {
            try {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var text = http.GetStringAsync($"http://127.0.0.1:{_config.CdpPort}/json").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(text);
                foreach (var t in doc.RootElement.EnumerateArray()) {
                    if (t.TryGetProperty("type", out var type) && type.GetString() != "page") continue;
                    if (t.TryGetProperty("url", out var url) && (url.GetString() ?? "").Contains("orpheus", StringComparison.OrdinalIgnoreCase)) return true;
                }
            } catch { }
            return false;
        }

        static void EnsureNcmCdp() {
            try {
                if (HasNcmCdpTarget()) {
                    WriteLog($">>> [CDP] 网易云调试目标已就绪（端口 {_config.CdpPort}）", ConsoleColor.Green);
                    return;
                }

                // Give a just-started NCM a moment to expose the page.
                for (int i = 0; i < 10; i++) {
                    Thread.Sleep(500);
                    if (HasNcmCdpTarget()) {
                        WriteLog($">>> [CDP] 网易云调试目标已就绪（端口 {_config.CdpPort}）", ConsoleColor.Green);
                        return;
                    }
                }

                var exePath = ResolveNcmExe();
                var procs = Process.GetProcessesByName("cloudmusic");
                if (procs.Length > 0) {
                    WriteLog(">>> [CDP] 网易云正在运行但没有调试端口，准备重启", ConsoleColor.Yellow);
                    foreach (var p in procs) { try { p.Kill(); } catch { } }
                    Thread.Sleep(1200);
                }

                if (string.IsNullOrWhiteSpace(exePath)) {
                    WriteLog(">>> [错误] 找不到网易云安装路径，请填写根目录 ncm-path.txt 或先手动运行网易云。", ConsoleColor.Red);
                    return;
                }

                Process.Start(exePath, $"--remote-debugging-port={_config.CdpPort}");
                WriteLog($">>> [CDP] 已启动网易云并带调试端口({_config.CdpPort})", ConsoleColor.Green);
            } catch (Exception ex) {
                WriteLog($"[网易云 CDP 检查失败] {ex.Message}", ConsoleColor.Red);
            }
        }

        static async Task<string> SendCDPCommandAndGetResultAsync(string script)
        {
            if (!_config.EnableCDP) return null;
            
            try {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                var jsonStr = await httpClient.GetStringAsync($"http://127.0.0.1:{_config.CdpPort}/json");
                var targets = JsonDocument.Parse(jsonStr).RootElement;
                
                string wsUrl = "";
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString().Contains("orpheus")) {
                        wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(wsUrl)) {
                     foreach (var t in targets.EnumerateArray()) {
                        if (t.GetProperty("type").GetString() == "page") {
                            wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(wsUrl)) return null;

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                
                var payload = new {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new Dictionary<string, object> {
                        { "expression", script },
                        { "returnByValue", true },
                        { "awaitPromise", true }
                    }
                };
                
                string reqJson = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(reqJson);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                
                var buffer = new byte[8192];
                using var ms = new MemoryStream();
                WebSocketReceiveResult wsRes;
                do {
                    wsRes = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    ms.Write(buffer, 0, wsRes.Count);
                } while (!wsRes.EndOfMessage);
                
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                
                string resJson = Encoding.UTF8.GetString(ms.ToArray());
                var doc = JsonDocument.Parse(resJson);
                
                if (doc.RootElement.TryGetProperty("result", out var outerRes) && 
                    outerRes.TryGetProperty("result", out var innerRes) && 
                    innerRes.TryGetProperty("value", out var val)) 
                {
                    if (val.ValueKind == JsonValueKind.String) return val.GetString();
                    return val.GetRawText();
                }
                
                return "true";
            } catch {
                return null;
            }
        }

        // ====== 更新的 Fiber Store 挖掘算法 (加入了 Debug Log 与新版 React 兼容) ======
        static string GetFiberStoreExtractJs() {
            return @"
                function _ensureStore() {
                    window.__debug_store_log = window.__debug_store_log || [];
                    const log = (msg) => { window.__debug_store_log.push(msg); };
                    
                    if (window._reduxStore) { log('Store缓存已存在'); return true; }
                    
                    const portal = document.querySelector('#portal_root');
                    const rootEl = document.querySelector('#root');
                    const realRoot = portal && portal._reactRootContainer && portal._reactRootContainer._internalRoot;
                    if (realRoot) {
                        window._fiberRoot = realRoot;
                        if (rootEl && portal._reactRootContainer) rootEl._reactRootContainer = portal._reactRootContainer;
                    }
                    const root = window._fiberRoot || realRoot || (rootEl && rootEl._reactRootContainer && rootEl._reactRootContainer._internalRoot);
                    
                    if (!root) { 
                        log('找不到 React 根节点，网易云页面可能还在加载中...'); 
                        return false; 
                    }

                    log('正在顺藤摸瓜，往下挖 Redux Store...');
                    let queue = [root.current || root];
                    let visited = 0;
                    
                    while (queue.length > 0) {
                        let node = queue.shift();
                        if (!node) continue;
                        visited++;
                        if (visited > 20000) { log('防死循环: 遍历超过 20000 节点，强制终止'); break; }
                        
                        if (node.memoizedProps && node.memoizedProps.store) { 
                            window._reduxStore = node.memoizedProps.store; 
                            log(`🎉 抓到了！在第 ${visited} 节点的 (memoizedProps) 中提取到 Store`); 
                            return true; 
                        }
                        if (node.stateNode && node.stateNode.store) { 
                            window._reduxStore = node.stateNode.store; 
                            log(`🎉 抓到了！在第 ${visited} 节点的 (stateNode) 中提取到 Store`); 
                            return true; 
                        }
                        
                        let child = node.child;
                        while (child) { queue.push(child); child = child.sibling; }
                    }
                    log(`😭 失败: 遍历了整棵树，没找到 store...`);
                    return false;
                }
            ";
        }

        static bool HasAnotherPendingSong(string songId) {
            return _targetQueue.Any(s => string.Equals(s.Id, songId, StringComparison.Ordinal));
        }

        static bool IsCurrentSongId(string songId) {
            return _currentPlayingSong.HasValue && string.Equals(_currentPlayingSong.Value.Id, songId, StringComparison.Ordinal);
        }

        static async Task RemovePendingSongAsync(string songId) {
            if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit)) {
                WriteLog($"[CDP] 删除待播歌曲失败: 无效歌曲 ID ({songId})", ConsoleColor.Yellow);
                return;
            }
            var before = await NcmCdp.GetStateAsync(_config.CdpPort);
            if (before?["current"]?["id"]?.ToString() == songId) {
                WriteLog($"[CDP] 待播歌曲就是当前歌，跳过删除: id={songId}", ConsoleColor.Gray);
                return;
            }
            var result = await NcmCdp.RemoveSongAsync(_config.CdpPort, songId);
            if (result == null) {
                WriteLog($"[CDP] 删除待播歌曲失败: id={songId} (CDP 无响应)", ConsoleColor.Yellow);
                return;
            }
            bool ok = result["ok"]?.Value<bool>() ?? false;
            bool removed = result["removed"]?.Value<bool>() ?? false;
            string error = result["error"]?.ToString() ?? "";
            string count = result["removedCount"]?.ToString() ?? "0";
            if (!ok) {
                WriteLog($"[CDP] 删除待播歌曲失败: id={songId} error={error}", ConsoleColor.Yellow);
            } else if (removed) {
                WriteLog($"[CDP] 已删除待播歌曲: id={songId} count={count}", ConsoleColor.Cyan);
            } else {
                WriteLog($"[CDP] 待播歌曲已不在 NCM: id={songId}", ConsoleColor.Gray);
            }
        }

        static async Task RemoveSongFromPlaylistViaCDP(string songId) {
            if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit)) {
                WriteLog($"[CDP] 删除歌曲失败: 无效歌曲 ID ({songId})", ConsoleColor.Yellow);
                return;
            }
            string js = GetFiberStoreExtractJs() + $@"
                if (_ensureStore()) {{
                    window._reduxStore.dispatch({{
                        type: 'async:action/doAction',
                        payload: {{ actionId: 'removeFromPlayingList', data: {{ id: String({songId}) }} }}
                    }});
                    window._reduxStore.dispatch({{
                        type: 'async:action/doAction',
                        payload: {{ actionId: 'removeFromPlayList', data: {{ id: String({songId}) }} }}
                    }});
                }}
            ";
            var result = await SendCDPCommandAndGetResultAsync(js);
            if (result == null) WriteLog($"[CDP] 删除歌曲失败: id={songId}", ConsoleColor.Yellow);
        }

        static readonly string[] BiliCookieKeys = { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "buvid3", "buvid4", "sid" };

        static bool IsBiliHost(string url)
        {
            try
            {
                var host = new Uri(url).Host;
                return host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("biligame.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".biligame.com", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void MergeBiliCookie(Dictionary<string, string> cookies, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value)) return;
            switch (name.Trim())
            {
                case "SESSDATA":
                case "bili_jct":
                case "DedeUserID":
                case "DedeUserID__ckMd5":
                case "buvid3":
                case "buvid4":
                case "sid":
                    cookies[name.Trim()] = value.Trim();
                    break;
            }
        }

        static IEnumerable<string> GetSetCookieHeaders(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) return values;
            if (response.Content != null && response.Content.Headers.TryGetValues("Set-Cookie", out values)) return values;
            return Array.Empty<string>();
        }

        static void MergeBiliCookiesFromSetCookie(Dictionary<string, string> cookies, IEnumerable<string> headers)
        {
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header)) continue;
                var first = header.Split(';', 2)[0].Trim();
                int eq = first.IndexOf('=');
                if (eq <= 0) continue;
                MergeBiliCookie(cookies, first.Substring(0, eq), first.Substring(eq + 1));
            }
        }

        static void MergeBiliCookiesFromUrl(Dictionary<string, string> cookies, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri == null) return;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                string key;
                try { key = Uri.UnescapeDataString(kv[0]); } catch { key = kv[0]; }
                // Keep the cookie value URL-encoded: SESSDATA can contain %2C.
                MergeBiliCookie(cookies, key, kv[1]);
            }
        }

        static void MergeBiliCookiesFromJar(Dictionary<string, string> cookies, CookieContainer jar)
        {
            var urls = new[]
            {
                "https://bilibili.com/",
                "https://www.bilibili.com/",
                "https://passport.bilibili.com/",
                "https://api.bilibili.com/",
                "https://biligame.com/",
                "https://www.biligame.com/",
                "https://passport.biligame.com/"
            };
            foreach (var url in urls)
            {
                try
                {
                    foreach (Cookie cookie in jar.GetCookies(new Uri(url)))
                        MergeBiliCookie(cookies, cookie.Name, cookie.Value);
                }
                catch { }
            }
        }

        static string BuildBiliCookieHeader(Dictionary<string, string> cookies)
        {
            var parts = new List<string>();
            foreach (var key in BiliCookieKeys)
            {
                if (cookies.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                    parts.Add($"{key}={value}");
            }
            return string.Join("; ", parts);
        }

        static bool HasBiliSessionCookie(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return false;
            return cookie.Split(';').Any(part => part.Trim().StartsWith("SESSDATA=", StringComparison.Ordinal));
        }

        static async Task<(bool ok, long uid, string name, string error)> VerifyBiliNavAsync(string cookie)
        {
            try
            {
                using var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                var json = await client.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
                var doc = JObject.Parse(json);
                int code = doc["code"]?.Value<int>() ?? -1;
                var data = doc["data"] as JObject;
                bool isLogin = data?["isLogin"]?.Value<bool>() ?? false;
                long mid = data?["mid"]?.Value<long>() ?? 0;
                string uname = data?["uname"]?.Value<string>() ?? "";
                if (code == 0 && isLogin && mid > 0) return (true, mid, uname, "");
                return (false, mid, uname, $"nav code={code} isLogin={isLogin}");
            }
            catch (Exception ex)
            {
                return (false, 0, "", ex.Message);
            }
        }

        static async Task BiliQrLoginAsync()
        {
            _isQrLoggingIn = true; _qrCodeBase64 = ""; _qrLoginStatus = "正在向 B站请求二维码...";
            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = cookieJar,
                    AutomaticDecompression = DecompressionMethods.All
                };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

                var genRes = await client.GetStringAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header");
                var genDoc = JObject.Parse(genRes);
                string url = genDoc["data"]?["url"]?.ToString() ?? "";
                string qrcodeKey = genDoc["data"]?["qrcode_key"]?.ToString() ?? "";

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(qrcodeKey))
                {
                    _qrLoginStatus = "错误：无法获取二维码";
                    WriteLog("[登录] 二维码接口没有返回 url/qrcode_key", ConsoleColor.Red);
                    return;
                }

                string qrApiUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=256x256&data={Uri.EscapeDataString(url)}";
                var qrBytes = await client.GetByteArrayAsync(qrApiUrl);
                _qrCodeBase64 = "data:image/png;base64," + Convert.ToBase64String(qrBytes);
                _qrLoginStatus = "请使用手机 B站 APP 扫码";
                WriteLog(">>> [登录] 请扫码...", ConsoleColor.Cyan);

                bool loginSuccess = false;

                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(2000);
                    using var pollResp = await client.GetAsync($"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header");
                    var setCookies = GetSetCookieHeaders(pollResp).ToList();
                    var pollRes = await pollResp.Content.ReadAsStringAsync();
                    var pollDoc = JObject.Parse(pollRes);
                    int code = pollDoc["data"]?["code"]?.Value<int>() ?? -1;

                    if (code == 86101) continue;
                    if (code == 86090) { _qrLoginStatus = "已扫码，请在手机上点击确认"; continue; }
                    if (code == 86038) { _qrLoginStatus = "二维码已失效，请重新发起"; break; }
                    if (code != 0) continue;

                    _qrLoginStatus = "扫码成功！正在换取登录凭证...";
                    var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
                    MergeBiliCookiesFromSetCookie(cookies, setCookies);

                    string crossDomainUrl = pollDoc["data"]?["url"]?.ToString() ?? "";
                    MergeBiliCookiesFromUrl(cookies, crossDomainUrl);

                    if (!string.IsNullOrWhiteSpace(crossDomainUrl) && IsBiliHost(crossDomainUrl))
                    {
                        try
                        {
                            using var crossResp = await client.GetAsync(crossDomainUrl);
                            MergeBiliCookiesFromSetCookie(cookies, GetSetCookieHeaders(crossResp).ToList());
                            await crossResp.Content.ReadAsStringAsync();
                        }
                        catch (Exception ex)
                        {
                            WriteLog("[登录] 跨域确认失败: " + ex.Message, ConsoleColor.Yellow);
                        }
                    }

                    MergeBiliCookiesFromJar(cookies, cookieJar);
                    string cookieHeader = BuildBiliCookieHeader(cookies);
                    if (!HasBiliSessionCookie(cookieHeader))
                    {
                        _qrLoginStatus = "登录失败：未获取到 SESSDATA，请重新扫码";
                        WriteLog($"[登录] 二维码已确认，但未获取到 SESSDATA (keys={string.Join(",", cookies.Keys)})", ConsoleColor.Yellow);
                        break;
                    }

                    long uid = 0;
                    if (cookies.TryGetValue("DedeUserID", out var uidText))
                        long.TryParse(uidText, out uid);

                    var nav = await VerifyBiliNavAsync(cookieHeader);
                    if (nav.ok)
                    {
                        uid = nav.uid;
                    }
                    else if (nav.error.StartsWith("nav code=-101", StringComparison.Ordinal)
                        || nav.error.StartsWith("nav code=0 isLogin=False", StringComparison.Ordinal))
                    {
                        _qrLoginStatus = "登录失败：B站校验未通过，请重新扫码";
                        WriteLog("[登录] nav 校验未通过: " + nav.error, ConsoleColor.Yellow);
                        break;
                    }
                    else
                    {
                        WriteLog("[登录] nav 校验暂时不可用，继续使用 Cookie: " + nav.error, ConsoleColor.Yellow);
                    }

                    if (uid <= 0)
                    {
                        _qrLoginStatus = "登录失败：未获取到 UID，请重新扫码";
                        WriteLog("[登录] 二维码已确认，但 UID 为 0", ConsoleColor.Yellow);
                        break;
                    }

                    _config.BiliCookie = cookieHeader;
                    _config.BiliUid = uid;
                    SaveConfig();

                    _qrLoginStatus = "登录完成，请连接直播间！";
                    WriteLog($">>> [登录] 登录成功，UID: {uid} Cookie: {string.Join(",", cookies.Keys)}", ConsoleColor.Green);
                    loginSuccess = true;
                    break;
                }

                if (!loginSuccess && !_qrLoginStatus.Contains("失败") && !_qrLoginStatus.Contains("失效"))
                {
                    _qrLoginStatus = "登录失败：二维码等待超时，请重新扫码";
                }
            }
            catch (Exception ex)
            {
                _qrLoginStatus = $"错误: {ex.Message}";
                WriteLog($"[登录] 二维码登录失败: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                _isQrLoggingIn = false;
            }
        }

        static void SetLiveStatus(int status, string source) {
            int previous = _liveStatus;
            _liveStatus = status;
            _liveStatusSource = source;
            _liveStatusAt = DateTime.Now;
            if (previous == status) return;

            string label = status == 1 ? "直播中"
                : status == 0 ? "未开播"
                : status == 2 ? "轮播"
                : $"未知({status})";
            WriteLog($">>> [直播状态] {label}（{source}）", status == 1 ? ConsoleColor.Green : ConsoleColor.Yellow);
            if (status != 1) {
                WriteLog(">>> [提示] 当前不是直播中状态；权限、房管标记和真实观众行为建议开播后再验证", ConsoleColor.Yellow);
            }
        }

        static void StartLiveStatusMonitor(long roomId) {
            try { _liveStatusCts?.Cancel(); } catch { }
            _liveStatusCts = new CancellationTokenSource();
            var ct = _liveStatusCts.Token;
            _ = Task.Run(() => MonitorLiveStatusAsync(roomId, ct));
        }

        static async Task MonitorLiveStatusAsync(long roomId, CancellationToken ct) {
            int delaySeconds = 60;
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                } catch (OperationCanceledException) {
                    break;
                }

                try {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    var json = await client.GetStringAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}", ct);
                    var doc = JObject.Parse(json);
                    int code = doc["code"]?.Value<int>() ?? -1;
                    if (code == 0) {
                        int status = doc["data"]?["live_status"]?.Value<int>() ?? -1;
                        SetLiveStatus(status, "poll");
                        delaySeconds = 60;
                    } else {
                        string msg = doc["msg"]?.ToString() ?? "";
                        WriteLog($"[直播状态] 刷新失败: code={code} msg={msg}", ConsoleColor.DarkGray);
                        delaySeconds = Math.Min(delaySeconds * 2, 300);
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    WriteLog("[直播状态] 刷新失败: " + ex.Message, ConsoleColor.DarkGray);
                    delaySeconds = Math.Min(delaySeconds * 2, 300);
                }
            }
        }

        static async Task<bool> ConnectToLiveRoom(long shortRoomId) {
            _lastRoomError = "";
            try {
                if (!HasBiliSessionCookie(_config.BiliCookie)) {
                    _lastRoomError = "not_logged_in";
                    WriteLog(">>> [连接] 未登录或登录 Cookie 不完整，请先扫码登录", ConsoleColor.Yellow);
                    return false;
                }
                if (_config.BiliUid <= 0) {
                    _lastRoomError = "login_incomplete";
                    WriteLog(">>> [连接] 登录 UID 无效，请重新扫码登录", ConsoleColor.Yellow);
                    return false;
                }

                WriteLog($">>> [连接] 正在尝试解析直播间 {shortRoomId} ...", ConsoleColor.Cyan);

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", _config.BiliCookie);

                var roomInitRes = await client.GetStringAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={shortRoomId}");
                var roomInitDoc = JObject.Parse(roomInitRes);
                
                if (roomInitDoc["code"]?.Value<int>() != 0) {
                    _lastRoomError = "room_init_failed";
                    WriteLog($">>> [错误] 初始化房间失败: code={roomInitDoc["code"]} msg={roomInitDoc["msg"]}", ConsoleColor.Red);
                    return false;
                }

                long realRoomId = shortRoomId;
                if (roomInitDoc["data"]?.Type == JTokenType.Object) {
                    realRoomId = roomInitDoc["data"]["room_id"]?.Value<long>() ?? shortRoomId;
                }

                int liveStatus = roomInitDoc["data"]?["live_status"]?.Value<int>() ?? -1;
                WriteLog($">>> [连接] 房间 {shortRoomId}（真实房间 {realRoomId}）live_status={liveStatus}", ConsoleColor.Gray);
                SetLiveStatus(liveStatus, "room_init");

                var danmuReqUrl = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={realRoomId}&type=0";
                var danmuJson = await client.GetStringAsync(danmuReqUrl);
                
                if (danmuJson.Contains("-352")) {
                    var fallbackReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id={realRoomId}&platform=pc&player=web");
                    fallbackReq.Headers.TryAddWithoutValidation("Cookie", _config.BiliCookie);
                    fallbackReq.Headers.TryAddWithoutValidation("Referer", $"https://live.bilibili.com/{shortRoomId}");
                    var fbRes = await client.SendAsync(fallbackReq);
                    danmuJson = await fbRes.Content.ReadAsStringAsync();
                }

                var danmuDoc = JObject.Parse(danmuJson);
                int danmuCode = danmuDoc["code"]?.Value<int>() ?? -1;
                string token = danmuDoc["data"]?["token"]?.ToString() ?? "";
                if (danmuCode != 0 || string.IsNullOrWhiteSpace(token)) {
                    _lastRoomError = "danmu_token_failed";
                    WriteLog($">>> [错误] 获取弹幕 token 失败: code={danmuCode} msg={danmuDoc["msg"]}", ConsoleColor.Red);
                    return false;
                }

                string finalBuvid = "999E9060-EA3F-0F79-7BDC-A14879D11DCB95434infoc";
                var b3Match = System.Text.RegularExpressions.Regex.Match(_config.BiliCookie, @"buvid3=([^;]+)");
                if (b3Match.Success) finalBuvid = b3Match.Groups[1].Value;

                var authObj = new {
                    uid = _config.BiliUid, roomid = realRoomId, protover = 3, buvid = finalBuvid,
                    support_ack = true, queue_uuid = Guid.NewGuid().ToString("N").Substring(0, 8),
                    scene = "room", platform = "web", type = 2, key = token
                };

                _config.AuthJson = JsonSerializer.Serialize(authObj);
                _config.RoomId = shortRoomId; 
                SaveConfig();
                
                WriteLog($">>> [成功] 直播间 {shortRoomId}（真实房间 {realRoomId}）弹幕 token 获取成功，已发起 WebSocket 连接。", ConsoleColor.Green);
                RestartBiliConnection();
                StartLiveStatusMonitor(shortRoomId);
                return true;

            } catch (Exception ex) {
                _lastRoomError = "exception";
                WriteLog($">>> [异常] 连接直播间出错: {ex.Message}", ConsoleColor.Red);
                return false;
            }
        }

        static bool LoadConfig() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(path)) return false;
                _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
                return true;
            } catch (Exception ex) {
                WriteLog($"[配置] 读取 config.json 失败: {ex.Message}", ConsoleColor.Yellow);
                return false;
            }
        }

        static void SaveConfig() {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"), JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { WriteLog($"[配置] 保存 config.json 失败: {ex.Message}", ConsoleColor.Red); }
        }

        static void EnforceRestrictedCommandPermissions() {
            bool changed = false;
            void Restrict(PermissionConfig perm, string label) {
                if (perm == null) return;
                if (perm.MinGuardType == 0 && perm.MinMedalLevel == 0) {
                    perm.MinGuardType = -1;
                    changed = true;
                    WriteLog($"[配置] 已收紧{label}权限：仅主播/房管/SuperUser", ConsoleColor.Yellow);
                }
            }
            // Old configs defaulted these management commands to everyone.
            Restrict(_config.ToggleAcceptPermission, "开启/关闭点歌");
            Restrict(_config.PriorityPermission, "置顶点歌/置顶");
            Restrict(_config.ForceControlPermission, "移除");
            if (changed) SaveConfig();
        }

        static bool HasPermission(UserInfo user, PermissionConfig perm) {
            if (_config.SuperUsers.Contains(user.Name)) return true;
            // Streamer and room admins are always allowed to use management commands.
            if (IsStreamerOrModerator(user)) return true;
            if (perm.AllowManager && user.IsManager) return true;
            if (perm.MinGuardType == -1) return false; 
            if (perm.MinGuardType > 0) return user.GuardType > 0 && user.GuardType <= perm.MinGuardType;
            if (perm.MinMedalLevel > 0 && user.MedalLevel >= perm.MinMedalLevel) {
                if (string.IsNullOrEmpty(perm.MedalName) || user.MedalName == perm.MedalName) return true;
            } else if (perm.MinMedalLevel == 0) return true; 
            return false;
        }

        static async Task<string> GetUserAvatarAsync(string uid) {
            if (string.IsNullOrEmpty(uid)) return "";
            if (_avatarCache.TryGetValue(uid, out string url)) return url;
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                var res = await client.GetStringAsync($"https://api.bilibili.com/x/web-interface/card?mid={uid}");
                var doc = JsonDocument.Parse(res);
                if (doc.RootElement.GetProperty("code").GetInt32() == 0) {
                    string face = doc.RootElement.GetProperty("data").GetProperty("card").GetProperty("face").GetString();
                    if (!string.IsNullOrEmpty(face)) {
                        if (face.StartsWith("http:")) face = face.Replace("http:", "https:");
                        face += "@64w_64h_1c.webp"; _avatarCache[uid] = face; return face;
                    }
                }
            } catch { }
            string fallback = $"https://api.dicebear.com/7.x/identicon/svg?seed={uid}";
            _avatarCache[uid] = fallback; return fallback;
        }

        static async Task TryRequestSong(UserInfo user, string keyword, string mode = "normal", int prioritySeq = 0) {
            if (!_acceptingRequests) {
                WriteLog($"   |_ [拒绝] {user.Name} 点歌失败: 点歌已关闭", ConsoleColor.DarkGray);
                return;
            }
            if (mode == "normal" && !HasPermission(user, _config.OrderPermission)) {
                WriteLog($"   |_ [拒绝] {user.Name} 弹幕点歌失败: 权限不足", ConsoleColor.DarkGray);
                return;
            }
            if (!_config.SuperUsers.Contains(user.Name) && _userCooldowns.TryGetValue(user.Uid, out DateTime last) && (DateTime.Now - last).TotalMinutes < _config.CooldownMinutes) {
                WriteLog($"   |_ [拒绝] {user.Name} 处于冷却中", ConsoleColor.DarkGray);
                return;
            }

            string avatar = await GetUserAvatarAsync(user.Uid);
            try {
                var res = await _neteaseApi.RequestAsync(CloudMusicApiProviders.Search, new Dictionary<string, object> { { "keywords", keyword }, { "limit", 1 } });
                var songs = res["result"]?["songs"] as JArray;
                if (songs == null || songs.Count == 0) {
                    WriteLog($"   |_ [失败] 未找到歌曲: \"{keyword}\"", ConsoleColor.Red);
                    return;
                }

                var s = songs[0];
                var newSong = new SongInfo { 
                    Id = s["id"]?.ToString(), SongName = s["name"]?.ToString(), 
                    ArtistName = string.Join("/", s["artists"]?.Select(a => (string)a["name"])), 
                    OrderedBy = user.Name, OrderedByUid = user.Uid, OrderedByAvatar = avatar,
                    Priority = prioritySeq > 0, PrioritySeq = prioritySeq
                };
                
                lock (_queueLock) {
                    if (mode == "top") {
                        _targetQueue.Add(newSong);
                        WriteLog($"[指令] {user.Name} 置顶点歌了: {newSong.SongName}", ConsoleColor.Green);
                        SyncHUD($"[置顶] {newSong.SongName}");
                        MarkQueueDirty();
                    } else {
                        _targetQueue.Add(newSong);
                        WriteLog($"   |_ [入队] {newSong.FullTitle} (by {user.Name})", ConsoleColor.Green);
                        SyncHUD($"[入队] {newSong.SongName} ({user.Name})");
                        MarkQueueDirty();
                    }
                }

                _userCooldowns[user.Uid] = DateTime.Now;
            } catch (Exception ex) {
                WriteLog($"[点歌] 搜索或入队失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        static bool IsStreamerOrModerator(UserInfo user) {
            if (user.IsManager) return true;
            var ownerUid = _config.BiliUid > 0 ? _config.BiliUid.ToString() : "";
            return ownerUid.Length > 0 && string.Equals(user.Uid, ownerUid, StringComparison.Ordinal);
        }

        static void TrySkipSong(UserInfo user) {
            if (!IsStreamerOrModerator(user)) {
                WriteLog($"   |_ [拒绝] {user.Name} 尝试切歌，仅主播和房管可用", ConsoleColor.DarkGray);
                return;
            }
            WriteLog($"   |_ [指令] {user.Name} 发起了切歌", ConsoleColor.Yellow);
            SyncHUD($"[切歌] 由 {user.Name} 发起");
            _ = TrySkipSongAsync();
        }

        static string CurrentNcmKey(JObject? state) {
            var current = state?["current"];
            if (current == null) return "";
            return $"{current["id"]}|{current["qid"]}|{current["start"]}";
        }

        static async Task TrySkipSongAsync() {
            if (!_config.EnableCDP) {
                WriteLog("[CDP] 切歌未执行: CDP 已禁用", ConsoleColor.Yellow);
                return;
            }

            var before = await NcmCdp.GetStateAsync(_config.CdpPort);
            string beforeKey = CurrentNcmKey(before);

            // Prefer clicking the visible NCM next button. The old Redux
            // action name is not stable across NCM releases.
            string clickJs = @"
                (() => {
                    const visible = (el) => {
                        try {
                            const r = el.getBoundingClientRect();
                            const s = getComputedStyle(el);
                            return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
                        } catch (e) { return false; }
                    };
                    const buttons = Array.from(document.querySelectorAll('.btnc-nxt, [title*=""下一首""]'));
                    const btn = buttons.find(visible);
                    if (!btn) return JSON.stringify({ok:false,error:'no_next_button'});
                    btn.click();
                    return JSON.stringify({ok:true,via:'dom'});
                })()
            ";

            var clickResult = await NcmCdp.EvaluateAsync(clickJs, _config.CdpPort, 5000, false);
            bool dispatched = clickResult != null && clickResult.Contains("\"ok\":true");
            string via = "dom";

            if (!dispatched) {
                string fallbackJs = GetFiberStoreExtractJs() + @"
                    if (_ensureStore()) {
                        window._reduxStore.dispatch({
                            type: 'async:action/doAction',
                            payload: { actionId: 'playNext', data: { eventType: 'click' } }
                        });
                        'dispatched';
                    } else {
                        'no_store';
                    }
                ";
                var fallbackResult = await NcmCdp.EvaluateAsync(fallbackJs, _config.CdpPort, 5000, false);
                dispatched = fallbackResult != null && fallbackResult.Contains("dispatched");
                via = "redux";
                if (!dispatched) {
                    string domText = clickResult ?? "null";
                    string reduxText = fallbackResult ?? "null";
                    WriteLog($"[CDP] 切歌失败: 未找到下一首按钮，旧 action 也无响应 (dom={domText}, redux={reduxText})", ConsoleColor.Yellow);
                    return;
                }
            }

            await Task.Delay(1200);
            var after = await NcmCdp.GetStateAsync(_config.CdpPort);
            string afterKey = CurrentNcmKey(after);
            if (!string.IsNullOrEmpty(beforeKey) && afterKey != beforeKey) {
                WriteLog($"[CDP] 切歌成功 ({via}): {beforeKey} -> {afterKey}", ConsoleColor.Cyan);
            } else if (!string.IsNullOrEmpty(beforeKey) && afterKey == beforeKey) {
                WriteLog($"[CDP] 切歌未生效 ({via}): current 仍为 {beforeKey}", ConsoleColor.Yellow);
            } else {
                WriteLog($"[CDP] 切歌结果不确定 ({via}): before={beforeKey} after={afterKey}", ConsoleColor.Yellow);
            }
        }

        static void HandleDanmaku(byte[] bytes) {
            try {
                var json = Encoding.UTF8.GetString(bytes); using var doc = JsonDocument.Parse(json);
                string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";

                if (cmd == "LIVE") {
                    SetLiveStatus(1, "WS LIVE");
                    return;
                }
                if (cmd == "PREPARING") {
                    SetLiveStatus(0, "WS PREPARING");
                    return;
                }
                
                if (cmd.StartsWith("DANMU_MSG")) {
                    var info = doc.RootElement.GetProperty("info");
                    string msg = (info[1].ValueKind == JsonValueKind.String ? info[1].GetString() : info[1].ToString()) ?? "";
                    msg = msg.Trim();
                    var userBase = info[2];
                    string rawUid = userBase[0].ValueKind == JsonValueKind.Number ? userBase[0].GetInt64().ToString() : userBase[0].ToString().Trim('"');
                    // DANMU_MSG.info[2][2] is Bilibili's room-admin flag.
                    bool isRoomAdmin = false;
                    try {
                        isRoomAdmin = userBase[2].ValueKind == JsonValueKind.Number
                            ? userBase[2].GetInt32() == 1
                            : userBase[2].ToString().Trim('"') == "1";
                    } catch { }
                    UserInfo u = new UserInfo { Uid = rawUid, Name = userBase[1].GetString(), IsManager = isRoomAdmin, GuardType = info.GetArrayLength() > 7 ? info[7].GetInt32() : 0 };
                    var medal = info[3]; if (medal.GetArrayLength() >= 2) { u.MedalLevel = medal[0].GetInt32(); u.MedalName = medal[1].GetString(); }

                    string biliTs = "";
                    try {
                        var meta = info[0];
                        if (meta.ValueKind == JsonValueKind.Array && meta.GetArrayLength() > 4) {
                            var ts = meta[4];
                            if (ts.ValueKind == JsonValueKind.Number) biliTs = ts.GetInt64().ToString();
                        }
                    } catch { }
                    string medalText = u.MedalLevel > 0 ? $"{u.MedalLevel}:{u.MedalName}" : "";
                    DanmakuLog.Append($"room={_config.RoomId} uid={rawUid} user={u.Name ?? ""} admin={(isRoomAdmin ? 1 : 0)} guard={u.GuardType} medal={medalText} bili_ts={biliTs} text={EscapeForLog(msg, 500)}");
                    
                    if (_config.ShowAllDanmaku) { WriteLog($"[弹幕] {u.Name}: {msg}", ConsoleColor.DarkGray); }

                    string lowerMsg = msg.ToLower();
                    
                    if (lowerMsg == "test" || lowerMsg == "ping" || lowerMsg == "测试" || lowerMsg == "测试连接") {
                        WriteLog($"[通讯] 收到来自 {u.Name} 的 Test 弹幕测试！(内容: {msg})", ConsoleColor.Cyan);
                        OverlayState.LastToastMsg = $"收到 {u.Name} 的连接测试";
                        OverlayState.LastToastTime = DateTime.Now.Ticks; 
                        return;
                    }

                    if (msg == "开启点歌" || msg == "关闭点歌") {
                        if (HasPermission(u, _config.ToggleAcceptPermission)) {
                            _acceptingRequests = (msg == "开启点歌");
                            WriteLog($"[指令] {u.Name} 通过弹幕{msg}", ConsoleColor.Yellow);
                            SyncHUD();
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试{msg}，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("置顶点歌")) {
                        if (HasPermission(u, _config.PriorityPermission)) {
                            if (!_acceptingRequests) {
                                WriteLog($"   |_ [拒绝] {u.Name} 点歌失败: 点歌已关闭", ConsoleColor.DarkGray);
                                return;
                            }
                            string kw = msg.Replace("置顶点歌", "").Trim();
                            if (!string.IsNullOrEmpty(kw)) {
                                int seq = Interlocked.Increment(ref _topSeq);
                                _ = Task.Run(() => TryRequestSong(u, kw, "top", seq));
                            }
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试置顶点歌，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("移除")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Substring(2).Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    MarkQueueDirty(); 
                                    WriteLog($"[指令] {u.Name} 移除了待播歌曲 {target.SongName}", ConsoleColor.Yellow);
                                    SyncHUD($"[移除] {target.SongName}");
                                    bool keepCopy = HasAnotherPendingSong(target.Id);
                                    bool isCurrent = IsCurrentSongId(target.Id);
                                    if (keepCopy) WriteLog($"   |_ [保留] {target.SongName} 仍有其他人的点歌，保留 NCM 副本", ConsoleColor.Gray);
                                    else if (isCurrent) WriteLog($"   |_ [保留] {target.SongName} 是当前歌，保留 NCM 副本", ConsoleColor.Gray);
                                    else _ = RemovePendingSongAsync(target.Id);
                                }
                            }
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试移除待播歌曲，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("置顶") || msg.StartsWith("优先")) {
                        if (HasPermission(u, _config.PriorityPermission)) {
                            string kw = msg.Replace("置顶", "").Replace("优先", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx >= 0) {
                                    var target = _targetQueue[idx];
                                    if (idx > 0) {
                                        _targetQueue.RemoveAt(idx);
                                        _targetQueue.Insert(0, target);
                                    }
                                    MarkQueueDirty(); 
                                    int prioritySeq = Interlocked.Increment(ref _topSeq);
                                    _companionEngine?.Prioritize(target.Id, prioritySeq);
                                    WriteLog($"[指令] {u.Name} 置顶了 {target.SongName}", ConsoleColor.Green);
                                    SyncHUD($"[置顶] {target.SongName}");
                                } else {
                                    WriteLog($"   |_ [失败] 置顶未找到目标: {kw}", ConsoleColor.Red);
                                }
                            }
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试置顶，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("撤回") || msg.StartsWith("取消")) {
                        if (HasPermission(u, _config.CancelPermission)) {
                            string kw = msg.Replace("撤回", "").Replace("取消", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid && (s.SongName.ToLower().Contains(kw) || s.ArtistName.ToLower().Contains(kw)));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    MarkQueueDirty(); 
                                    WriteLog($"   |_ [撤回] {u.Name} 已移除待播歌曲: {target.SongName}", ConsoleColor.Yellow);
                                    SyncHUD($"[撤回] 成功"); 
                                    
                                    bool keepCopy = HasAnotherPendingSong(target.Id);
                                    bool isCurrent = IsCurrentSongId(target.Id);
                                    if (keepCopy) WriteLog($"   |_ [保留] {target.SongName} 仍有其他人的点歌，保留 NCM 副本", ConsoleColor.Gray);
                                    else if (isCurrent) WriteLog($"   |_ [保留] {target.SongName} 是当前歌，保留 NCM 副本", ConsoleColor.Gray);
                                    else _ = RemovePendingSongAsync(target.Id);
                                } 
                                else if (_currentPlayingSong.HasValue && _currentPlayingSong.Value.OrderedByUid == u.Uid && (string.IsNullOrEmpty(kw) || _currentPlayingSong.Value.SongName.ToLower().Contains(kw))) 
                                {
                                    if (!IsStreamerOrModerator(u)) {
                                        WriteLog($"   |_ [拒绝] {u.Name} 尝试撤回当前播放的歌曲，仅主播和房管可用", ConsoleColor.DarkGray);
                                        return;
                                    }
                                    var target = _currentPlayingSong.Value;
                                    WriteLog($"   |_ [撤回] 正在撤回当前播放的歌曲并自动切歌: {target.SongName}", ConsoleColor.Yellow);
                                    
                                    _currentPlayingSong = null;
                                    _ = TrySkipSongAsync();
                                    _ = RemoveSongFromPlaylistViaCDP(target.Id);
                                }
                            }
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试撤回歌曲，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("点歌")) { 
                        if (!_acceptingRequests) {
                            WriteLog($"   |_ [拒绝] {u.Name} 点歌失败: 点歌已关闭", ConsoleColor.DarkGray);
                            return;
                        }
                        string kw = msg.Substring(2).Trim(); 
                        if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw, "normal")); 
                    }
                    else if (msg == "切歌" || msg == "跳过") TrySkipSong(u);
                }
            } catch (Exception ex) {
                string raw = "";
                try { raw = EscapeForLog(Encoding.UTF8.GetString(bytes), 240); } catch { }
                WriteLog("[弹幕] 解析失败: " + ex.Message, ConsoleColor.Yellow);
                DanmakuLog.Append($"parse-error err={ex.Message} raw={raw}");
            }
        }

        static async Task StartBiliConnection(CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(_config.AuthJson)) return;
            while (!ct.IsCancellationRequested) {
                using var ws = new ClientWebSocket();
                try {
                    WriteLog(">>> [网络] 正在连接 B站弹幕服务器...", ConsoleColor.DarkGray);
                    await ws.ConnectAsync(new Uri("wss://broadcastlv.chat.bilibili.com/sub"), ct);
                    
                    byte[] packet = new byte[16 + _config.AuthJson.Length];
                    BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0), packet.Length); BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4), 16); BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6), 1); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 7); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 1); Buffer.BlockCopy(Encoding.UTF8.GetBytes(_config.AuthJson), 0, packet, 16, _config.AuthJson.Length);
                    await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
                    
                    WriteLog(">>> [网络] 弹幕握手包已发送，监听开始。", ConsoleColor.Green);

                    _ = Task.Run(async () => { while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) { byte[] p = new byte[16 + 15]; BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), p.Length); BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(4), 16); BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(6), 1); BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), 2); BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(12), 1); Buffer.BlockCopy(Encoding.UTF8.GetBytes("[object Object]"), 0, p, 16, 15); await ws.SendAsync(new ArraySegment<byte>(p), WebSocketMessageType.Binary, true, ct); await Task.Delay(30000, ct); } }, ct);
                    byte[] buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                        using var ms = new MemoryStream(); WebSocketReceiveResult res;
                        do { res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); ms.Write(buffer, 0, res.Count); } while (!res.EndOfMessage);
                        if (ms.Length > 0) {
                            var data = ms.ToArray();
                            int offset = 0;
                            while (offset + 16 <= data.Length) {
                                int pLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                                if (pLen < 16 || pLen > data.Length - offset) {
                                    WriteLog($"[弹幕] 不完整的外层包: offset={offset} len={pLen} total={data.Length}", ConsoleColor.Yellow);
                                    break;
                                }
                                var outerPacket = new byte[pLen];
                                Buffer.BlockCopy(data, offset, outerPacket, 0, pLen);
                                ProcessBiliPacket(outerPacket, 0);
                                offset += pLen;
                            }
                            if (offset < data.Length) {
                                var tail = new byte[data.Length - offset];
                                Buffer.BlockCopy(data, offset, tail, 0, tail.Length);
                                ProcessBiliPayload(tail);
                            }
                        }
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch(Exception e) { 
                    if (ct.IsCancellationRequested || _shutdownRequested != 0) {
                        WriteLog("[网络] 正在退出，停止弹幕连接", ConsoleColor.Gray);
                        break;
                    }
                    WriteLog($"[网络断开] {e.Message}，5秒后重试...", ConsoleColor.Red); 
                    try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }
        static string EscapeForLog(string text, int max = 200) {
            if (string.IsNullOrEmpty(text)) return "";
            var clean = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return clean.Length <= max ? clean : clean.Substring(0, max) + "...";
        }

        static void ProcessBiliPacket(byte[] packet, int depth) {
            try {
                if (packet == null || packet.Length < 16) return;
                if (depth > 4) {
                    WriteLog("[弹幕] 压缩包嵌套过深，已跳过", ConsoleColor.Yellow);
                    return;
                }
                int pLen = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(0, 4));
                int hLen = BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(4, 2));
                int protocol = BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(6, 2));
                int op = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4));
                if (pLen < hLen || pLen > packet.Length || hLen < 16) {
                    WriteLog($"[弹幕] 包长度异常: pLen={pLen} hLen={hLen} total={packet.Length}", ConsoleColor.Yellow);
                    return;
                }
                if (op != 5) return;
                var body = new byte[pLen - hLen];
                Buffer.BlockCopy(packet, hLen, body, 0, body.Length);

                if (protocol == 2) {
                    using var input = new MemoryStream(body);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    ProcessNestedBiliPackets(output.ToArray(), depth + 1);
                } else if (protocol == 3) {
                    using var input = new MemoryStream(body);
                    using var brotli = new BrotliStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    brotli.CopyTo(output);
                    ProcessNestedBiliPackets(output.ToArray(), depth + 1);
                } else {
                    ProcessBiliPayload(body);
                }
            } catch (Exception ex) {
                WriteLog("[弹幕] 包解析失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        static void ProcessNestedBiliPackets(byte[] data, int depth) {
            if (data == null || data.Length == 0) return;
            int offset = 0;
            int packetCount = 0;
            bool parsedAny = false;
            while (offset + 16 <= data.Length) {
                int pLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                int hLen = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset + 4, 2));
                if (pLen < 16 || hLen < 16 || pLen > data.Length - offset) break;
                var packet = new byte[pLen];
                Buffer.BlockCopy(data, offset, packet, 0, pLen);
                ProcessBiliPacket(packet, depth);
                parsedAny = true;
                packetCount++;
                offset += pLen;
            }
            if (packetCount > 1) {
                WriteLog($"[弹幕] 批量包已解析 {packetCount} 个内层包", ConsoleColor.DarkGray);
            }
            if (offset < data.Length) {
                var tail = new byte[data.Length - offset];
                Buffer.BlockCopy(data, offset, tail, 0, tail.Length);
                // Some servers return a plain JSON body instead of nested packets.
                if (tail.Length > 0 && (tail[0] == (byte)'{' || tail[0] == (byte)'[')) ProcessBiliPayload(tail);
                else if (!parsedAny) WriteLog($"[弹幕] 未识别的压缩包内容: len={data.Length}", ConsoleColor.Yellow);
            }
        }

        static List<string> ExtractBiliJsonMessages(string text) {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            try {
                var token = JToken.Parse(text);
                if (token is JArray array) {
                    foreach (var item in array) result.Add(item.ToString(Newtonsoft.Json.Formatting.None));
                } else {
                    result.Add(token.ToString(Newtonsoft.Json.Formatting.None));
                }
                return result;
            } catch { }

            foreach (var part in System.Text.RegularExpressions.Regex.Split(text, "[\x00-\x1f]+")) {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                try {
                    var token = JToken.Parse(trimmed);
                    result.Add(token.ToString(Newtonsoft.Json.Formatting.None));
                } catch (Exception ex) {
                    WriteLog($"[弹幕] JSON 解析失败: len={trimmed.Length} prefix={EscapeForLog(trimmed, 120)} err={ex.Message}", ConsoleColor.Yellow);
                }
            }
            if (result.Count == 0) {
                WriteLog($"[弹幕] 未解析出消息: len={text.Length} prefix={EscapeForLog(text, 120)}", ConsoleColor.Yellow);
            }
            return result;
        }

        static void ProcessBiliPayload(byte[] payload) {
            if (payload == null || payload.Length == 0) return;
            try {
                var text = Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n', '\t');
                if (text.Length == 0) return;
                foreach (var json in ExtractBiliJsonMessages(text)) {
                    try { HandleDanmaku(Encoding.UTF8.GetBytes(json)); }
                    catch (Exception ex) { WriteLog("[弹幕] 消息处理失败: " + ex.Message, ConsoleColor.Yellow); }
                }
            } catch (Exception ex) {
                WriteLog("[弹幕] payload 解析失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        static void RestartBiliConnection() { if (!string.IsNullOrWhiteSpace(_config.AuthJson)) { _biliCts.Cancel(); _biliCts = new CancellationTokenSource(); _ = Task.Run(() => StartBiliConnection(_biliCts.Token)); } }

        static async Task<string> GetCDPWebSocketUrl() {
            try {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                var jsonStr = await httpClient.GetStringAsync($"http://127.0.0.1:{_config.CdpPort}/json");
                var targets = JsonDocument.Parse(jsonStr).RootElement;
                
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString().Contains("orpheus")) {
                        return t.GetProperty("webSocketDebuggerUrl").GetString();
                    }
                }
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page") {
                        return t.GetProperty("webSocketDebuggerUrl").GetString();
                    }
                }
            } catch { }
            return null;
        }

    }
}