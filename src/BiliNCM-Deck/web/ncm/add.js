(async () => {
  const id = '__SONG_ID__';
  const mode = '__OFFSET_MODE__';
  const explicitOffset = __OFFSET__;
  const pendingQids = __PENDING_QIDS__;
  const m = window.ctl && window.ctl.defPlayer && window.ctl.defPlayer.MF;
  if (!m) return JSON.stringify({ok:false, error:'no_MF', href:location.href});

  const idOf = (x) => {
    if (!x) return '';
    if (x.tid != null) return String(x.tid);
    if (x.id != null) return String(x.id);
    if (x.track && x.track.id != null) return String(x.track.id);
    return '';
  };
  const qidOf = (x) => {
    if (!x) return '';
    if (x.id != null) return String(x.id);
    if (x.tid != null) return String(x.tid);
    if (x.track && x.track.id != null) return String(x.track.id);
    return '';
  };
  const readQueue = () => {
    try { const a = m.V('track_queue'); return Array.isArray(a) ? a.slice() : []; }
    catch (e) { return []; }
  };
  const readPlaying = () => { try { return m.U(); } catch (e) { return null; } };
  const wid = (x) => qidOf(x);
  const curInfo = (q) => {
    const u = readPlaying();
    let curId = '';
    let curWrapper = '';
    let curStart = '';
    let curState = null;
    if (u && u.data && u.data.id != null) {
      curId = String(u.data.id);
      if (u.from) {
        if (u.from.id != null) curWrapper = String(u.from.id);
        if (u.from.startlogtime != null) curStart = String(u.from.startlogtime);
      }
      curState = u.state;
    }
    let index = curWrapper ? q.findIndex(x => wid(x) === curWrapper) : -1;
    if (index < 0 && curId) index = q.findIndex(x => idOf(x) === curId);
    return { id: curId, wrapper: curWrapper, start: curStart, state: curState, index: index };
  };
  const findAfter = (q, ci) => {
    if (ci.index < 0) return null;
    for (let i = ci.index + 1; i < q.length; i++) {
      if (idOf(q[i]) === id) return { index: i, qid: wid(q[i]) };
    }
    return null;
  };
  const countOf = (q) => q.reduce((n, x) => n + (idOf(x) === id ? 1 : 0), 0);
  const computeAnchor = (q, ci) => {
    if (mode === 'omit') return undefined;
    if (mode === 'append') return q.length;
    if (mode === 'explicit') return Math.max(0, explicitOffset);
    if (ci.index < 0) return undefined;
    const pending = {};
    (pendingQids || []).forEach(x => { pending[String(x)] = true; });
    let last = -1;
    for (let i = ci.index + 1; i < q.length; i++) {
      if (pending[wid(q[i])]) last = i;
    }
    return last >= 0 ? last + 1 : ci.index + 1;
  };
  const mergeCurrent = (q, ci, name) => JSON.stringify({
    ok: true,
    added: false,
    existed: true,
    mergedCurrent: true,
    started: false,
    id: id,
    name: name || (ci.id ? '' : ''),
    qid: ci.wrapper,
    position: ci.index,
    currentIndex: ci.index,
    current: { id: ci.id, state: ci.state },
    queueLen: q.length,
    occurrences: countOf(q),
    anchor: null,
    error: null
  });

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  let q0 = readQueue();
  let ci0 = curInfo(q0);
  if (ci0.id === id) return mergeCurrent(q0, ci0, '');

  // A copy already waiting after the current song is the same request.
  // Never add a second copy; the caller can move it if it is not at the
  // pending anchor yet.
  const existing0 = findAfter(q0, ci0);
  if (existing0) {
    return JSON.stringify({
      ok: true,
      added: false,
      existed: true,
      alreadyPending: true,
      mergedCurrent: false,
      started: false,
      id: id,
      name: '',
      qid: existing0.qid,
      position: existing0.index,
      currentIndex: ci0.index,
      current: { id: ci0.id, state: ci0.state },
      queueLen: q0.length,
      occurrences: countOf(q0),
      anchor: null,
      error: null
    });
  }

  let response;
  let data;
  let abortTimer = null;
  try {
    const controller = new AbortController();
    abortTimer = setTimeout(() => controller.abort(), 8000);
    response = await fetch('https://music.163.com/api/v3/song/detail?c=' + encodeURIComponent(JSON.stringify([{id:Number(id)}])), {credentials:'include', signal:controller.signal});
    data = await response.json();
  } catch (e) {
    return JSON.stringify({ok:false, error:'detail_fetch_failed', detail:String(e), id:id});
  } finally {
    if (abortTimer) clearTimeout(abortTimer);
  }
  const raw = data && data.songs && data.songs[0];
  if (!raw) {
    return JSON.stringify({ok:false, error:'detail_not_found', status:response && response.status, id:id});
  }
  const name = raw.name || '';
  const track = Object.assign({}, raw, {
    artists: raw.ar || raw.artists || [],
    album: raw.al || raw.album || {},
    duration: raw.dt || raw.duration || 0,
    mvid: raw.mv || raw.mvid || 0,
    alias: raw.alia || raw.alias || []
  });
  try { m.K(track); } catch (e) { }

  // Re-read immediately before the synchronous insert so the anchor is
  // based on the live current index, not a value from an earlier poll.
  const q1 = readQueue();
  const ci1 = curInfo(q1);
  if (ci1.id === id) return mergeCurrent(q1, ci1, name);
  const existing1 = findAfter(q1, ci1);
  if (existing1) {
    return JSON.stringify({
      ok: true,
      added: false,
      existed: true,
      alreadyPending: true,
      mergedCurrent: false,
      started: false,
      id: id,
      name: name,
      qid: existing1.qid,
      position: existing1.index,
      currentIndex: ci1.index,
      current: { id: ci1.id, state: ci1.state },
      queueLen: q1.length,
      occurrences: countOf(q1),
      anchor: null,
      error: null
    });
  }
  const anchor = computeAnchor(q1, ci1);

  const options = {
    fid: 10,
    data: '',
    href: '',
    text: 'BiliNCM',
    nickName: '',
    userId: '',
    fromButton: false
  };
  try {
    if (typeof m.Xn !== 'function') throw new Error('no_Xn');
    m.Xn(track, options, {
      mode: 'normal',
      play: false,
      offset: anchor
    });
  } catch (e) {
    return JSON.stringify({ok:false, error:'queue_update_failed', detail:String(e && (e.stack || e)), id:id});
  }

  await sleep(900);
  const q2 = readQueue();
  const ci2 = curInfo(q2);
  const before = {};
  q1.forEach(x => { before[wid(x)] = true; });
  let addedEntry = null;
  for (let i = 0; i < q2.length; i++) {
    if (idOf(q2[i]) === id && !before[wid(q2[i])]) { addedEntry = { index: i, qid: wid(q2[i]) }; break; }
  }
  if (!addedEntry && anchor !== undefined && anchor >= 0 && anchor < q2.length && idOf(q2[anchor]) === id) {
    addedEntry = { index: anchor, qid: wid(q2[anchor]) };
  }
  if (!addedEntry) {
    const pos = q2.findIndex(x => idOf(x) === id);
    if (pos >= 0) addedEntry = { index: pos, qid: wid(q2[pos]) };
  }
  return JSON.stringify({
    ok: !!addedEntry,
    added: true,
    existed: false,
    alreadyPending: false,
    mergedCurrent: false,
    started: ci2.id === id,
    id: id,
    name: name,
    qid: addedEntry ? addedEntry.qid : '',
    position: addedEntry ? addedEntry.index : -1,
    currentIndex: ci2.index,
    current: { id: ci2.id, state: ci2.state },
    queueLen: q2.length,
    occurrences: countOf(q2),
    anchor: anchor === undefined ? null : anchor,
    currentChanged: (ci1.wrapper !== ci2.wrapper || ci1.start !== ci2.start),
    error: addedEntry ? null : 'queue_not_updated'
  });
})()