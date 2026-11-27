using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// C# port of BiliNCM-Companion.ps1's queue state machine.
    ///
    /// The bot queue is only a request inbox. The NCM playing list is the
    /// authority. Known requests carry a pending wrapper qid, so a manual
    /// copy of the same song never becomes a pending d by accident.
    /// </summary>
    internal sealed class CompanionQueueEngine
    {
        internal sealed class BotSnapshot
        {
            public List<Program.SongInfo> Queue = new List<Program.SongInfo>();
            public Program.SongInfo? Current;
            public bool Playing;
            public bool Accepting = true;
        }

        internal sealed class SongView
        {
            public string id { get; set; } = "";
            public string name { get; set; } = "";
            public string artists { get; set; } = "";
            public string badge { get; set; } = "manual";
            public string orderer { get; set; } = "";
        }

        internal sealed class StateView
        {
            public bool bot { get; set; }
            public bool ncm { get; set; }
            public bool accepting { get; set; } = true;
            public bool botAutoPlay { get; set; }
            public string alert { get; set; } = "";
            public SongView? current { get; set; }
            public List<SongView> pending { get; set; } = new List<SongView>();
            public int pendingCount { get; set; }
        }

        private sealed class KnownItem
        {
            public string Id = "";
            public string Name = "";
            public string Artists = "";
            public string OrderedBy = "";
            public string OrderedByUid = "";
            public string OrderedByAvatar = "";
            public bool IsD;
            public bool Priority;
            public int PrioritySeq;
            public string State = "Legacy";
            public string PendingQid = "";
            public int Seq;
            public int Absent;
            public int BotAbsent;
            public DateTime RetryAt = DateTime.MinValue;
            public bool ReRequested;
        }

        private sealed class NcmItem
        {
            public string Id = "";
            public string Qid = "";
            public string Name = "";
            public string Artists = "";
        }

        private sealed class NcmSnapshot
        {
            public bool Ok;
            public string Page = "";
            public NcmItem? Current;
            public int CurrentIndex = -1;
            public string CurrentWrapper = "";
            public List<NcmItem> Queue = new List<NcmItem>();
            public Dictionary<string, int> QidIndex = new Dictionary<string, int>();
            public Dictionary<string, List<int>> TidIndices = new Dictionary<string, List<int>>();
        }

        private sealed class BotQueueInfo
        {
            public List<string> RawOrder = new List<string>();
            public Dictionary<string, int> Counts = new Dictionary<string, int>();
            public Dictionary<string, Program.SongInfo> ById = new Dictionary<string, Program.SongInfo>();
        }

        private readonly Func<BotSnapshot> _getBot;
        private readonly Func<int> _getPort;
        private readonly Action<Program.SongInfo?> _setCurrent;
        private readonly Action<string, ConsoleColor> _log;
        private readonly Action _pauseBot;

        private readonly Dictionary<string, KnownItem> _known = new Dictionary<string, KnownItem>();
        private readonly Dictionary<string, int> _rawCounts = new Dictionary<string, int>();
        private readonly List<string> _backlog = new List<string>();
        private readonly ConcurrentQueue<(string Id, int Seq)> _priorityRequests = new ConcurrentQueue<(string Id, int Seq)>();
        private readonly Dictionary<string, int> _priorityPending = new Dictionary<string, int>();
        private readonly object _stateLock = new object();
        private StateView _state = new StateView();

        private bool _sessionReady;
        private bool? _lastNcmOk;
        private string _lastNcmPage = "";
        private DateTime _lastNcmRestartAt = DateTime.MinValue;
        private bool _ncmRestartHandled = true;
        private DateTime _lastPauseAt = DateTime.MinValue;
        private int _requestSeq;
        private string _alert = "";
        private bool _alertLogged;

        public CompanionQueueEngine(
            Func<BotSnapshot> getBot,
            Func<int> getPort,
            Action<Program.SongInfo?> setCurrent,
            Action<string, ConsoleColor> log,
            Action pauseBot)
        {
            _getBot = getBot;
            _getPort = getPort;
            _setCurrent = setCurrent;
            _log = log;
            _pauseBot = pauseBot;
        }

        public StateView Snapshot()
        {
            lock (_stateLock) return _state;
        }

        /// <summary>
        /// Queue an explicit "move to the next slot after current" request.
        /// Used by the streamer's 置顶 command without racing the poll loop.
        /// </summary>
        public void Prioritize(string songId, int prioritySeq)
        {
            if (!string.IsNullOrWhiteSpace(songId))
                _priorityRequests.Enqueue((songId, prioritySeq));
        }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex)
                {
                    _log("Companion loop error: " + ex.Message, ConsoleColor.Red);
                }
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task TickAsync()
        {
            var bot = _getBot();
            if (bot == null)
            {
                PublishState(null, null, false);
                return;
            }

            bool botAutoPlay = bot.Playing;
            if (botAutoPlay)
            {
                if (!bot.Current.HasValue)
                {
                    if ((DateTime.Now - _lastPauseAt).TotalSeconds >= 3)
                    {
                        _lastPauseAt = DateTime.Now;
                        try { _pauseBot(); } catch { }
                        _log("bot auto-play paused (overlay mode requires it)", ConsoleColor.Green);
                        _alert = "";
                        _alertLogged = false;
                    }
                }
                else
                {
                    _alert = "bot_autoplay_on";
                    if (!_alertLogged)
                    {
                        _log("bot auto-play is ON with an adopted current song; not pausing to avoid a cut", ConsoleColor.Yellow);
                        _alertLogged = true;
                    }
                }
            }
            else
            {
                _alert = "";
                _alertLogged = false;
            }

            NcmSnapshot? ncm = await GetNcmStateAsync();
            bool ncmOk = ncm != null;
            if (_lastNcmOk != ncmOk)
            {
                _log(ncmOk ? "NCM state available" : "NCM state unavailable; waiting for CDP", ncmOk ? ConsoleColor.Green : ConsoleColor.Red);
                _lastNcmOk = ncmOk;
            }
            bool ncmRestartPending = false;
            bool restartBlocked = false;
            string currentId = ncm?.Current?.Id ?? "";
            int currentIndex = ncm?.CurrentIndex ?? -1;

            if (ncmOk)
            {
                if (_lastNcmPage.Length > 0 && ncm!.Page != _lastNcmPage)
                {
                    _log("NCM page changed (restart); reconciling pending d against the restored queue", ConsoleColor.Yellow);
                    _lastNcmRestartAt = DateTime.Now;
                    _ncmRestartHandled = false;
                }
                _lastNcmPage = ncm!.Page;
                ncmRestartPending = !_ncmRestartHandled;
            }

            var queueInfo = GetBotQueueInfo(bot);
            if (!_sessionReady)
            {
                foreach (var id in queueInfo.RawOrder)
                {
                    queueInfo.ById.TryGetValue(id, out var meta);
                    _known[id] = new KnownItem
                    {
                        Id = id,
                        Name = meta.SongName ?? "",
                        Artists = meta.ArtistName ?? "",
                        OrderedBy = meta.OrderedBy ?? "",
                        OrderedByUid = meta.OrderedByUid ?? "",
                        OrderedByAvatar = meta.OrderedByAvatar ?? "",
                        IsD = false,
                        State = "Legacy",
                        PendingQid = "",
                        Seq = 0,
                        Absent = 0,
                        BotAbsent = 0,
                        RetryAt = DateTime.MinValue,
                        ReRequested = false
                    };
                }
                _rawCounts.Clear();
                foreach (var pair in queueInfo.Counts) _rawCounts[pair.Key] = pair.Value;
                _sessionReady = true;
                _log($"session baseline: {_known.Count} existing bot queue entries treated as s/q", ConsoleColor.Green);
            }
            else
            {
                foreach (var id in queueInfo.RawOrder)
                {
                    int prev = _rawCounts.TryGetValue(id, out var prevCount) ? prevCount : 0;
                    int nowCount = queueInfo.Counts.TryGetValue(id, out var count) ? count : 0;
                    if (nowCount <= prev) continue;

                    queueInfo.ById.TryGetValue(id, out var meta);
                    bool incomingPriority = meta.Priority;
                    if (!_known.TryGetValue(id, out var k))
                    {
                        _requestSeq++;
                        _known[id] = new KnownItem
                        {
                            Id = id,
                            Name = meta.SongName ?? "",
                            Artists = meta.ArtistName ?? "",
                            OrderedBy = meta.OrderedBy ?? "",
                            OrderedByUid = meta.OrderedByUid ?? "",
                            OrderedByAvatar = meta.OrderedByAvatar ?? "",
                            IsD = true,
                            Priority = incomingPriority,
                            PrioritySeq = meta.PrioritySeq,
                            State = "New",
                            PendingQid = "",
                            Seq = _requestSeq,
                            Absent = 0,
                            BotAbsent = 0,
                            RetryAt = DateTime.MinValue,
                            ReRequested = false
                        };
                        AddBacklog(id);
                        _log($"request: id={id} name={meta.SongName} by={meta.OrderedBy}", ConsoleColor.Green);
                    }
                    else
                    {
                        if (id == currentId)
                        {
                            k.ReRequested = false;
                            _log($"request: id={id} merge (currently playing)", ConsoleColor.Green);
                        }
                        else if (k.State == "Pending" || k.State == "Playing")
                        {
                            k.ReRequested = true;
                            if (incomingPriority) {
                                k.Priority = true;
                                k.PrioritySeq = meta.PrioritySeq;
                                k.RetryAt = DateTime.MinValue;
                                AddBacklog(id);
                                _log($"request: id={id} prioritized (already pending/playing)", ConsoleColor.Green);
                            } else {
                                _log($"request: id={id} merge (already pending/playing)", ConsoleColor.Green);
                            }
                        }
                        else if (k.State == "New" || k.State == "Upgrade")
                        {
                            if (incomingPriority) {
                                k.Priority = true;
                                k.PrioritySeq = meta.PrioritySeq;
                                k.RetryAt = DateTime.MinValue;
                                AddBacklog(id);
                            }
                            _log($"request: id={id} merge (already queued for upgrade)", ConsoleColor.Green);
                        }
                        else
                        {
                            _requestSeq++;
                            k.IsD = true;
                            k.Priority = incomingPriority;
                            if (incomingPriority) k.PrioritySeq = meta.PrioritySeq;
                            k.State = "Upgrade";
                            k.PendingQid = "";
                            k.Absent = 0;
                            k.RetryAt = DateTime.MinValue;
                            k.ReRequested = false;
                            k.Seq = _requestSeq;
                            AddBacklog(id);
                            _log($"request: id={id} re-request -> move/upgrade to d", ConsoleColor.Green);
                        }
                    }
                }

                // A request deleted from the bot queue before it reached NCM
                // must be cancelled too.
                foreach (var k in _known.Values.ToList())
                {
                    if (k.State != "New" && k.State != "Upgrade") continue;
                    int nowCount = queueInfo.Counts.TryGetValue(k.Id, out var count) ? count : 0;
                    if (nowCount <= 0)
                    {
                        RemoveBacklog(k.Id);
                        k.State = "Done";
                        k.ReRequested = false;
                        _log($"request cancelled before queue add: id={k.Id} name={k.Name}", ConsoleColor.Green);
                    }
                }

                // A pending d withdrawn from the bot queue stops being a d.
                // The companion does not delete the NCM copy itself.
                foreach (var k in _known.Values.ToList())
                {
                    if (!(k.IsD && k.State == "Pending")) continue;
                    if (currentId.Length > 0 && k.Id == currentId)
                    {
                        k.BotAbsent = 0;
                        continue;
                    }
                    int nowCount = queueInfo.Counts.TryGetValue(k.Id, out var count) ? count : 0;
                    if (nowCount > 0)
                    {
                        k.BotAbsent = 0;
                        continue;
                    }
                    k.BotAbsent++;
                    if (k.BotAbsent >= 2)
                    {
                        RemoveBacklog(k.Id);
                        k.State = "Done";
                        k.PendingQid = "";
                        k.Absent = 0;
                        k.ReRequested = false;
                        _log($"pending d cancelled from bot queue: id={k.Id} name={k.Name}", ConsoleColor.Green);
                    }
                }

                _rawCounts.Clear();
                foreach (var pair in queueInfo.Counts) _rawCounts[pair.Key] = pair.Value;
                SortBacklog();
            }

            ApplyPriorityRequests(currentId);

            if (ncmOk)
            {
                ReconcileKnown(ncm!, ncmRestartPending);
            }

            // A re-request that arrived while an old d was finishing is
            // upgraded after reconciliation, so it never gets lost.
            foreach (var k in _known.Values.ToList())
            {
                if (!k.IsD) continue;
                if (k.State == "Done" && k.ReRequested)
                {
                    _requestSeq++;
                    k.State = "Upgrade";
                    k.PendingQid = "";
                    k.Absent = 0;
                    k.RetryAt = DateTime.MinValue;
                    k.ReRequested = false;
                    k.Seq = _requestSeq;
                    AddBacklog(k.Id);
                    _log($"request: id={k.Id} re-request after done -> upgrade", ConsoleColor.Green);
                }
                else if (k.State == "Pending" && k.ReRequested)
                {
                    if (string.IsNullOrWhiteSpace(k.PendingQid) || ncm == null || !ncm.QidIndex.ContainsKey(k.PendingQid))
                    {
                        _requestSeq++;
                        k.State = "Upgrade";
                        k.PendingQid = "";
                        k.Absent = 0;
                        k.RetryAt = DateTime.MinValue;
                        k.ReRequested = false;
                        k.Seq = _requestSeq;
                        AddBacklog(k.Id);
                        _log($"request: id={k.Id} re-request after pending copy vanished -> upgrade", ConsoleColor.Green);
                    }
                    else
                    {
                        k.ReRequested = false;
                    }
                }
                else if (k.State == "Playing" || k.State == "New" || k.State == "Upgrade")
                {
                    k.ReRequested = false;
                }
            }
            SortBacklog();

            if (ncmOk && ncmRestartPending)
            {
                double restartAge = _lastNcmRestartAt == DateTime.MinValue
                    ? double.MaxValue
                    : (DateTime.Now - _lastNcmRestartAt).TotalSeconds;
                bool restartReady = restartAge >= 8 &&
                    (ncm!.Queue.Count > 0 || string.IsNullOrWhiteSpace(currentId) || restartAge >= 30);
                if (!restartReady)
                {
                    restartBlocked = true;
                }
                else
                {
                    var missing = new List<KnownItem>();
                    foreach (var k in _known.Values.ToList())
                    {
                        if (!(k.IsD && k.State == "Pending")) continue;
                        if (currentId.Length > 0 && k.Id == currentId)
                        {
                            k.State = "Playing";
                            continue;
                        }
                        int pqIdx = -1;
                        if (!string.IsNullOrWhiteSpace(k.PendingQid) && ncm.QidIndex.TryGetValue(k.PendingQid, out var wrapperIdx))
                            pqIdx = wrapperIdx;
                        if (pqIdx >= 0)
                        {
                            if (currentIndex >= 0 && pqIdx == currentIndex) k.State = "Playing";
                            else if (currentIndex >= 0 && pqIdx < currentIndex) k.State = "Done";
                            continue;
                        }
                        if (ncm.TidIndices.TryGetValue(k.Id, out var indices) && currentIndex >= 0)
                        {
                            var after = indices.Where(x => x > currentIndex).ToList();
                            if (after.Count > 0)
                            {
                                k.PendingQid = ncm.Queue[after[0]].Qid;
                                k.Absent = 0;
                                continue;
                            }
                            var before = indices.Where(x => x < currentIndex).ToList();
                            if (before.Count > 0)
                            {
                                k.State = "Done";
                                continue;
                            }
                        }
                        missing.Add(k);
                    }

                    if (missing.Count > 0)
                    {
                        foreach (var k in missing.OrderBy(x => x.Seq))
                        {
                            k.State = "Upgrade";
                            k.PendingQid = "";
                            k.Absent = 0;
                            k.RetryAt = DateTime.MinValue;
                            k.ReRequested = false;
                            AddBacklog(k.Id);
                            _log($"NCM restart: re-adding missing pending d: id={k.Id} name={k.Name} seq={k.Seq}", ConsoleColor.Yellow);
                        }
                        SortBacklog();
                    }
                    _ncmRestartHandled = true;
                    ncmRestartPending = false;
                    _log("NCM restart reconciliation complete", ConsoleColor.Green);
                }
            }

            if (ncmOk && _sessionReady && !restartBlocked)
            {
                await ProcessOneBacklogAsync(ncm!);
            }

            PublishState(bot, ncm, botAutoPlay);
            UpdateProgramCurrent(ncm);
        }

        private async Task ProcessOneBacklogAsync(NcmSnapshot ncm)
        {
            var pendingQids = GetPendingQidList(ncm);
            int processIndex = -1;
            int priorityAnchor = ncm.CurrentIndex >= 0 ? ncm.CurrentIndex + 1 : -1;
            DateTime now = DateTime.Now;

            for (int i = 0; i < _backlog.Count; i++)
            {
                var id = _backlog[i];
                if (!_known.TryGetValue(id, out var k))
                {
                    _backlog.RemoveAt(i--);
                    continue;
                }
                if (k.State == "Done" || k.State == "Legacy")
                {
                    _backlog.RemoveAt(i--);
                    continue;
                }

                var currentId = ncm.Current?.Id ?? "";
                if (id == currentId)
                {
                    if (k.State == "New" || k.State == "Upgrade" || k.State == "Pending") k.State = "Playing";
                    k.Absent = 0;
                    k.ReRequested = false;
                    _backlog.RemoveAt(i--);
                    _log($"request: id={id} merge (currently playing)", ConsoleColor.Green);
                    continue;
                }

                if (k.State == "Pending" && !string.IsNullOrWhiteSpace(k.PendingQid) && ncm.QidIndex.TryGetValue(k.PendingQid, out var pqIdx))
                {
                    if (ncm.CurrentIndex < 0 || pqIdx > ncm.CurrentIndex)
                    {
                        if (!k.Priority)
                        {
                            _backlog.RemoveAt(i--);
                            continue;
                        }
                    }
                    if (ncm.CurrentIndex >= 0 && pqIdx == ncm.CurrentIndex)
                    {
                        k.State = "Playing";
                        k.Priority = false;
                        _backlog.RemoveAt(i--);
                        continue;
                    }
                }

                if (k.RetryAt > now) continue;
                processIndex = i;
                break;
            }

            if (processIndex < 0) return;

            var itemId = _backlog[processIndex];
            if (!_known.TryGetValue(itemId, out var item)) return;
            var occurrences = ncm.TidIndices.TryGetValue(itemId, out var occ) ? occ : new List<int>();
            bool handled = false;
            bool currentIsSong = ncm.Current != null && itemId == ncm.Current.Id;

            if (currentIsSong)
            {
                if (item.State == "New" || item.State == "Upgrade" || item.State == "Pending") item.State = "Playing";
                item.Absent = 0;
                item.Priority = false;
                item.ReRequested = false;
                _backlog.RemoveAt(processIndex);
                _log($"request: id={itemId} merge (currently playing)", ConsoleColor.Green);
                handled = true;
            }

            if (!handled && occurrences.Count > 0)
            {
                bool priorityMove = item.Priority && priorityAnchor >= 0;
                var move = await NcmCdp.MoveQueueItemAsync(_getPort(), itemId, priorityMove ? priorityAnchor : -1, priorityMove ? "explicit" : "pending", pendingQids);
                if (move != null && GetBool(move, "ok"))
                {
                    _backlog.RemoveAt(processIndex);
                    handled = true;
                    string movedCurrentId = GetString(move, "currentId");
                    if (GetBool(move, "mergedCurrent") || (movedCurrentId.Length > 0 && movedCurrentId == itemId))
                    {
                        item.State = "Playing";
                        item.Absent = 0;
                        item.Priority = false;
                        item.ReRequested = false;
                        _log($"request: id={itemId} merge (currently playing)", ConsoleColor.Green);
                    }
                    else if (GetInt(move, "currentIndex", -1) >= 0 && GetInt(move, "position", -1) <= GetInt(move, "currentIndex", -1))
                    {
                        item.State = "Upgrade";
                        item.PendingQid = "";
                        item.RetryAt = DateTime.Now.AddSeconds(5);
                        AddBacklog(itemId);
                        _log($"move did not land after current: id={itemId} pos={GetInt(move, "position", -1)} current={GetInt(move, "currentIndex", -1)}", ConsoleColor.Red);
                    }
                    else
                    {
                        item.IsD = true;
                        item.State = "Pending";
                        item.PendingQid = GetString(move, "qid");
                        item.Absent = 0;
                        item.Priority = false;
                        item.RetryAt = DateTime.MinValue;
                        item.ReRequested = false;
                        _log($"{(priorityMove ? "promoted" : "moved")} d: id={itemId} qid={item.PendingQid} from={GetInt(move, "from", -1)} anchor={GetInt(move, "anchor", -1)} pos={GetInt(move, "position", -1)} queue={GetInt(move, "queueLen", -1)}", ConsoleColor.Green);
                    }
                }
                else if (GetString(move, "error") == "not_in_queue")
                {
                    occurrences = new List<int>();
                }
                else
                {
                    item.RetryAt = DateTime.Now.AddSeconds(5);
                    _log($"move failed: id={itemId} error={GetString(move, "error")}", ConsoleColor.Yellow);
                    handled = true;
                }
            }

            if (!handled && occurrences.Count == 0)
            {
                bool priorityAdd = item.Priority && priorityAnchor >= 0;
                int anchor = priorityAdd ? priorityAnchor : GetPendingAnchor(ncm);
                var add = await NcmCdp.AddSongAsync(_getPort(), itemId, anchor, priorityAdd ? "explicit" : "pending", pendingQids);
                if (add != null && GetBool(add, "ok"))
                {
                    var addCurrent = add["current"] as JObject;
                    string addCurrentId = addCurrent != null ? GetString(addCurrent, "id") : "";
                    if (GetBool(add, "mergedCurrent") || GetBool(add, "started") || (addCurrentId.Length > 0 && addCurrentId == itemId))
                    {
                        item.State = "Playing";
                        item.Absent = 0;
                        item.Priority = false;
                        item.ReRequested = false;
                        _backlog.RemoveAt(processIndex);
                        _log($"request: id={itemId} started playing after add", ConsoleColor.Green);
                    }
                    else if (GetBool(add, "existed") && !GetBool(add, "added"))
                    {
                        bool priorityMove = item.Priority && priorityAnchor >= 0;
                        var move = await NcmCdp.MoveQueueItemAsync(_getPort(), itemId, priorityMove ? priorityAnchor : -1, priorityMove ? "explicit" : "pending", pendingQids);
                        if (move != null && GetBool(move, "ok") && (GetBool(move, "mergedCurrent") || GetString(move, "currentId") == itemId))
                        {
                            item.State = "Playing";
                            item.Absent = 0;
                            item.Priority = false;
                            item.ReRequested = false;
                            _backlog.RemoveAt(processIndex);
                            _log($"request: id={itemId} merge (currently playing)", ConsoleColor.Green);
                        }
                        else if (move != null && GetBool(move, "ok") && !(GetInt(move, "currentIndex", -1) >= 0 && GetInt(move, "position", -1) <= GetInt(move, "currentIndex", -1)))
                        {
                            item.State = "Pending";
                            item.PendingQid = GetString(move, "qid");
                            item.Absent = 0;
                            item.Priority = false;
                            item.RetryAt = DateTime.MinValue;
                            item.ReRequested = false;
                            _backlog.RemoveAt(processIndex);
                            _log($"{(priorityMove ? "promoted" : "moved")} d: id={itemId} qid={item.PendingQid} from={GetInt(move, "from", -1)} anchor={GetInt(move, "anchor", -1)} pos={GetInt(move, "position", -1)} queue={GetInt(move, "queueLen", -1)}", ConsoleColor.Green);
                        }
                        else
                        {
                            item.State = "Upgrade";
                            item.PendingQid = "";
                            item.RetryAt = DateTime.Now.AddSeconds(5);
                            _log($"existing copy move failed: id={itemId} error={GetString(move, "error")}", ConsoleColor.Yellow);
                        }
                    }
                    else
                    {
                        if (GetInt(add, "currentIndex", -1) >= 0 && GetInt(add, "position", -1) <= GetInt(add, "currentIndex", -1))
                        {
                            item.State = "Upgrade";
                            item.PendingQid = "";
                            item.RetryAt = DateTime.Now.AddSeconds(2);
                            AddBacklog(itemId);
                            _log($"add landed before current: id={itemId} pos={GetInt(add, "position", -1)} current={GetInt(add, "currentIndex", -1)}", ConsoleColor.Red);
                        }
                        else
                        {
                            item.IsD = true;
                            item.State = "Pending";
                            item.PendingQid = GetString(add, "qid");
                            item.Absent = 0;
                            item.Priority = false;
                            item.RetryAt = DateTime.MinValue;
                            item.ReRequested = false;
                            _backlog.RemoveAt(processIndex);
                            _log($"{(priorityAdd ? "promoted" : "queued")} d: id={itemId} anchor={GetInt(add, "anchor", -1)} pos={GetInt(add, "position", -1)} qid={item.PendingQid} queue={GetInt(add, "queueLen", -1)}", ConsoleColor.Green);
                        }
                    }
                }
                else
                {
                    item.RetryAt = DateTime.Now.AddSeconds(10);
                    _log($"queue add failed: id={itemId} error={GetString(add, "error")}", ConsoleColor.Yellow);
                }
            }

            SortBacklog();
        }

        private void ReconcileKnown(NcmSnapshot ncm, bool ncmRestartPending)
        {
            string currentId = ncm.Current?.Id ?? "";
            foreach (var k in _known.Values.ToList())
            {
                if (!k.IsD) continue;

                if (k.State == "Pending")
                {
                    int pqIdx = -1;
                    if (!string.IsNullOrWhiteSpace(k.PendingQid) && ncm.QidIndex.TryGetValue(k.PendingQid, out var wrapperIdx))
                        pqIdx = wrapperIdx;

                    if (pqIdx >= 0)
                    {
                        k.Absent = 0;
                        if (currentId.Length > 0 && k.Id == currentId)
                        {
                            k.State = "Playing";
                            _log($"playing: id={k.Id} name={k.Name} (pending d consumed)", ConsoleColor.Green);
                        }
                        else if (ncm.CurrentIndex >= 0 && pqIdx == ncm.CurrentIndex)
                        {
                            k.State = "Playing";
                            _log($"playing: id={k.Id} name={k.Name} (pending d consumed)", ConsoleColor.Green);
                        }
                        else if (ncm.CurrentIndex >= 0 && pqIdx < ncm.CurrentIndex)
                        {
                            k.State = "Done";
                            _log($"played: id={k.Id} name={k.Name} (d is now s-level, stays in list)", ConsoleColor.Green);
                        }
                        continue;
                    }

                    if (currentId.Length > 0 && k.Id == currentId)
                    {
                        k.State = "Playing";
                        k.Absent = 0;
                        if (ncm.CurrentWrapper.Length > 0) k.PendingQid = ncm.CurrentWrapper;
                        _log($"playing: id={k.Id} name={k.Name} (pending d consumed)", ConsoleColor.Green);
                        continue;
                    }

                    if (ncm.TidIndices.TryGetValue(k.Id, out var indices))
                    {
                        if (ncm.CurrentIndex >= 0)
                        {
                            var after = indices.Where(x => x > ncm.CurrentIndex).ToList();
                            if (after.Count > 0)
                            {
                                k.PendingQid = ncm.Queue[after[0]].Qid;
                                k.Absent = 0;
                                _log($"pending d wrapper changed; tracking new occurrence: id={k.Id} pos={after[0]}", ConsoleColor.Yellow);
                                continue;
                            }
                            var before = indices.Where(x => x < ncm.CurrentIndex).ToList();
                            if (before.Count > 0)
                            {
                                k.State = "Done";
                                k.Absent = 0;
                                _log($"played: id={k.Id} name={k.Name} (d is now s-level, stays in list)", ConsoleColor.Green);
                                continue;
                            }
                        }
                    }

                    if (ncmRestartPending) continue;
                    k.Absent++;
                    if (k.Absent >= 3)
                    {
                        k.State = "Done";
                        _log($"pending d left NCM queue without playing (cancelled/removed): id={k.Id} name={k.Name}", ConsoleColor.Yellow);
                    }
                }
                else if (k.State == "Playing")
                {
                    if (currentId.Length > 0 && k.Id == currentId)
                    {
                        k.Absent = 0;
                        continue;
                    }
                    bool played = false;
                    if (ncm.TidIndices.TryGetValue(k.Id, out var indices) && ncm.CurrentIndex >= 0)
                        played = indices.Any(x => x < ncm.CurrentIndex);
                    if (played)
                    {
                        k.State = "Done";
                        k.Absent = 0;
                        _log($"played: id={k.Id} name={k.Name} (d is now s-level, stays in list)", ConsoleColor.Green);
                        continue;
                    }
                    k.Absent++;
                    if (k.Absent >= 2)
                    {
                        k.State = "Done";
                        _log($"played: id={k.Id} name={k.Name} (left current)", ConsoleColor.Green);
                    }
                }
                else if (k.State == "New" || k.State == "Upgrade")
                {
                    if (currentId.Length > 0 && k.Id == currentId)
                    {
                        k.State = "Playing";
                        k.Absent = 0;
                        _log($"request: id={k.Id} merge (currently playing)", ConsoleColor.Green);
                    }
                }
            }
        }

        private List<string> GetPendingQidList(NcmSnapshot ncm)
        {
            var result = new List<string>();
            foreach (var k in _known.Values)
            {
                if (!(k.IsD && k.State == "Pending")) continue;
                if (string.IsNullOrWhiteSpace(k.PendingQid)) continue;
                if (!ncm.QidIndex.TryGetValue(k.PendingQid, out var idx)) continue;
                if (ncm.CurrentIndex >= 0 && idx <= ncm.CurrentIndex) continue;
                if (!result.Contains(k.PendingQid)) result.Add(k.PendingQid);
            }
            return result;
        }

        private int GetPendingAnchor(NcmSnapshot ncm)
        {
            if (ncm.CurrentIndex < 0) return -2;
            int last = -1;
            for (int i = ncm.CurrentIndex + 1; i < ncm.Queue.Count; i++)
            {
                var q = ncm.Queue[i];
                if (!_known.TryGetValue(q.Id, out var k)) continue;
                if (!(k.IsD && k.State == "Pending")) continue;
                if (!string.IsNullOrWhiteSpace(k.PendingQid) && k.PendingQid != q.Qid) continue;
                last = i;
            }
            return last >= 0 ? last + 1 : ncm.CurrentIndex + 1;
        }

        private void ApplyPriorityRequests(string currentId)
        {
            while (_priorityRequests.TryDequeue(out var queued))
            {
                if (!string.IsNullOrWhiteSpace(queued.Id))
                    _priorityPending[queued.Id] = queued.Seq;
            }

            foreach (var pair in _priorityPending.ToList())
            {
                string id = pair.Key;
                if (!_known.TryGetValue(id, out var k))
                    continue;

                if (id == currentId || k.State == "Playing" || k.State == "Done")
                {
                    _priorityPending.Remove(id);
                    continue;
                }

                if (k.State == "Legacy")
                {
                    k.IsD = true;
                    k.State = "Upgrade";
                    k.PendingQid = "";
                }
                k.Priority = true;
                k.PrioritySeq = pair.Value;
                k.RetryAt = DateTime.MinValue;
                AddBacklog(id);
                _priorityPending.Remove(id);
                _log($"priority request: id={id} name={k.Name} seq={k.PrioritySeq}", ConsoleColor.Green);
            }
        }

        private void AddBacklog(string id)
        {
            if (!_backlog.Contains(id)) _backlog.Add(id);
        }

        private void RemoveBacklog(string id)
        {
            while (_backlog.Contains(id)) _backlog.Remove(id);
        }

        private void SortBacklog()
        {
            if (_backlog.Count <= 1) return;
            var sorted = _backlog
                .OrderBy(id => _known.TryGetValue(id, out var k) && k.Priority ? 0 : 1)
                .ThenBy(id => {
                    if (!_known.TryGetValue(id, out var k)) return int.MaxValue;
                    return k.Priority ? k.PrioritySeq : k.Seq;
                })
                .ToList();
            _backlog.Clear();
            foreach (var id in sorted)
                if (!_backlog.Contains(id)) _backlog.Add(id);
        }

        private BotQueueInfo GetBotQueueInfo(BotSnapshot bot)
        {
            var info = new BotQueueInfo();
            foreach (var item in bot.Queue)
            {
                var id = item.Id ?? "";
                if (id.Length == 0 || !id.All(char.IsDigit)) continue;
                if (info.Counts.TryGetValue(id, out var count)) info.Counts[id] = count + 1;
                else
                {
                    info.Counts[id] = 1;
                    info.RawOrder.Add(id);
                }
                if (!info.ById.ContainsKey(id)) info.ById[id] = item;
                else if (item.Priority) {
                    var meta = info.ById[id];
                    meta.Priority = true;
                    if (item.PrioritySeq > meta.PrioritySeq) meta.PrioritySeq = item.PrioritySeq;
                    info.ById[id] = meta;
                }
            }
            return info;
        }

        private async Task<NcmSnapshot?> GetNcmStateAsync()
        {
            var raw = await NcmCdp.GetStateAsync(_getPort());
            if (raw == null || !GetBool(raw, "ok")) return null;
            var snap = new NcmSnapshot
            {
                Ok = true,
                Page = GetString(raw, "page"),
                CurrentIndex = GetInt(raw, "currentIndex", -1),
                CurrentWrapper = GetString(raw, "currentWrapper")
            };

            if (raw["current"] is JObject current)
            {
                var id = GetString(current, "id");
                if (id.Length > 0)
                {
                    snap.Current = new NcmItem
                    {
                        Id = id,
                        Name = GetString(current, "name"),
                        Artists = GetString(current, "artists"),
                        Qid = snap.CurrentWrapper
                    };
                }
            }

            if (raw["queue"] is JArray queue)
            {
                foreach (var token in queue)
                {
                    if (token is not JObject obj) continue;
                    var item = new NcmItem
                    {
                        Id = GetString(obj, "id"),
                        Qid = GetString(obj, "qid"),
                        Name = GetString(obj, "name"),
                        Artists = GetString(obj, "artists")
                    };
                    if (item.Id.Length == 0) continue;
                    int index = snap.Queue.Count;
                    snap.Queue.Add(item);
                    if (item.Qid.Length > 0 && !snap.QidIndex.ContainsKey(item.Qid))
                        snap.QidIndex[item.Qid] = index;
                    if (!snap.TidIndices.TryGetValue(item.Id, out var indices))
                    {
                        indices = new List<int>();
                        snap.TidIndices[item.Id] = indices;
                    }
                    indices.Add(index);
                }
            }

            return snap;
        }

        private void PublishState(BotSnapshot? bot, NcmSnapshot? ncm, bool botAutoPlay)
        {
            var state = new StateView
            {
                bot = bot != null,
                ncm = ncm != null,
                accepting = bot?.Accepting ?? true,
                botAutoPlay = botAutoPlay,
                alert = _alert
            };

            if (ncm != null)
            {
                if (ncm.Current != null)
                {
                    var k = _known.TryGetValue(ncm.Current.Id, out var known) ? known : null;
                    bool isD = k != null && k.IsD && (k.State == "Pending" || k.State == "Playing");
                    state.current = new SongView
                    {
                        id = ncm.Current.Id,
                        name = string.IsNullOrWhiteSpace(ncm.Current.Name) ? (k?.Name ?? "") : ncm.Current.Name,
                        artists = string.IsNullOrWhiteSpace(ncm.Current.Artists) ? (k?.Artists ?? "") : ncm.Current.Artists,
                        badge = isD ? "d" : "manual",
                        orderer = isD ? (k?.OrderedBy ?? "") : ""
                    };
                }

                var seen = new HashSet<string>();
                for (int i = 0; i < ncm.Queue.Count; i++)
                {
                    var q = ncm.Queue[i];
                    if (q.Qid.Length == 0) continue;
                    if (!seen.Add(q.Qid)) continue;
                    if (ncm.CurrentIndex >= 0)
                    {
                        if (i <= ncm.CurrentIndex) continue;
                    }
                    else if (ncm.Current != null && q.Id == ncm.Current.Id) continue;
                    if (!_known.TryGetValue(q.Id, out var k)) continue;
                    if (!(k.IsD && k.State == "Pending")) continue;
                    if (!string.IsNullOrWhiteSpace(k.PendingQid) && k.PendingQid != q.Qid) continue;
                    state.pending.Add(new SongView
                    {
                        id = q.Id,
                        name = string.IsNullOrWhiteSpace(q.Name) ? k.Name : q.Name,
                        artists = string.IsNullOrWhiteSpace(q.Artists) ? k.Artists : q.Artists,
                        orderer = k.OrderedBy,
                        badge = "d"
                    });
                }
            }
            state.pendingCount = state.pending.Count;
            lock (_stateLock) { _state = state; }
        }

        private void UpdateProgramCurrent(NcmSnapshot? ncm)
        {
            if (ncm?.Current == null)
            {
                _setCurrent(null);
                return;
            }
            var current = ncm.Current;
            _known.TryGetValue(current.Id, out var known);
            bool isD = known != null && known.IsD && (known.State == "Pending" || known.State == "Playing");
            _setCurrent(new Program.SongInfo
            {
                Id = current.Id,
                SongName = string.IsNullOrWhiteSpace(current.Name) ? (known?.Name ?? "") : current.Name,
                ArtistName = string.IsNullOrWhiteSpace(current.Artists) ? (known?.Artists ?? "") : current.Artists,
                OrderedBy = isD ? (known?.OrderedBy ?? "") : "",
                OrderedByUid = isD ? (known?.OrderedByUid ?? "") : "",
                OrderedByAvatar = isD ? (known?.OrderedByAvatar ?? "") : ""
            });
        }

        private static bool GetBool(JObject? obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return false;
            try { return token.Value<bool>(); } catch { return false; }
        }

        private static int GetInt(JObject? obj, string name, int fallback)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.Value<int>(); } catch { return fallback; }
        }

        private static string GetString(JObject? obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return "";
            return token.Type == JTokenType.String ? token.Value<string>() ?? "" : token.ToString();
        }
    }
}
