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

  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  const q0 = readQueue();
  const ci0 = curInfo(q0);
  const occurrences = [];
  for (let i = 0; i < q0.length; i++) {
    if (idOf(q0[i]) === id) occurrences.push(i);
  }
  if (ci0.id === id) {
    return JSON.stringify({
      ok: true,
      moved: false,
      noop: true,
      mergedCurrent: true,
      id: id,
      qid: ci0.wrapper,
      from: ci0.index,
      insertAt: ci0.index,
      anchor: null,
      position: ci0.index,
      currentIndex: ci0.index,
      currentId: ci0.id,
      currentChanged: false,
      queueLen: q0.length,
      error: null
    });
  }
  if (!occurrences.length) {
    return JSON.stringify({ok:false, error:'not_in_queue', id:id, qid:'', from:-1, insertAt:-1, anchor:null, position:-1, currentIndex:ci0.index, currentId:ci0.id, currentChanged:false, queueLen:q0.length});
  }

  // Prefer the first copy after the current song; otherwise use the most
  // recent copy in history. Never choose the current wrapper.
  let chosen = -1;
  for (let i = 0; i < occurrences.length; i++) {
    if (ci0.index >= 0 && occurrences[i] > ci0.index) { chosen = occurrences[i]; break; }
  }
  if (chosen < 0) {
    for (let i = 0; i < occurrences.length; i++) {
      if (ci0.index < 0 || occurrences[i] < ci0.index) chosen = occurrences[i];
    }
  }
  if (chosen < 0) {
    return JSON.stringify({ok:false, error:'only_current', id:id, qid:'', from:-1, insertAt:-1, anchor:null, position:-1, currentIndex:ci0.index, currentId:ci0.id, currentChanged:false, queueLen:q0.length});
  }
  const movedQid = wid(q0[chosen]);

  let anchor = null;
  const alreadyTrackedPending = (pendingQids || []).map(String).indexOf(movedQid) >= 0;
  if (mode === 'pending' && alreadyTrackedPending) {
    // This copy is already a tracked pending d. Its position among the
    // pending d items is the user-visible FIFO order, including manual
    // drags, so never reorder it just because it was asked again.
    anchor = chosen;
  } else if (mode === 'explicit') {
    anchor = Math.max(0, explicitOffset);
  } else if (mode === 'append') {
    anchor = q0.length;
  } else if (mode === 'omit') {
    anchor = q0.length;
  } else {
    if (ci0.index >= 0) {
      const pending = {};
      (pendingQids || []).forEach(x => { pending[String(x)] = true; });
      let last = -1;
      for (let i = ci0.index + 1; i < q0.length; i++) {
        // A move of a song that is already tracked as a pending d must
        // not count itself as the last pending item, otherwise a no-op
        // move would push it behind the later pending requests.
        if (wid(q0[i]) === movedQid) continue;
        if (pending[wid(q0[i])]) last = i;
      }
      anchor = last >= 0 ? last + 1 : ci0.index + 1;
    } else {
      anchor = q0.length;
    }
  }
  if (anchor > q0.length) anchor = q0.length;
  if (anchor < 0) anchor = 0;

  const insertAt = chosen < anchor ? anchor - 1 : anchor;
  if (insertAt === chosen) {
    return JSON.stringify({
      ok: true,
      moved: false,
      noop: true,
      mergedCurrent: false,
      id: id,
      qid: movedQid,
      from: chosen,
      insertAt: insertAt,
      anchor: anchor,
      position: chosen,
      currentIndex: ci0.index,
      currentId: ci0.id,
      currentChanged: false,
      queueLen: q0.length,
      error: null
    });
  }

  const ids = q0.map(x => wid(x));
  if (ids.some(x => !x)) {
    return JSON.stringify({ok:false, error:'empty_qid', id:id, qid:movedQid, from:chosen, insertAt:insertAt, anchor:anchor, position:-1, currentIndex:ci0.index, currentId:ci0.id, currentChanged:false, queueLen:q0.length});
  }
  const moved = ids.splice(chosen, 1)[0];
  const at = Math.max(0, Math.min(insertAt, ids.length));
  ids.splice(at, 0, moved);
  try {
    m.r4('track_queue', ids);
  } catch (e) {
    return JSON.stringify({ok:false, error:'move_failed', detail:String(e && (e.stack || e)), id:id, qid:movedQid, from:chosen, insertAt:at, anchor:anchor, position:-1, currentIndex:ci0.index, currentId:ci0.id, currentChanged:false, queueLen:q0.length});
  }

  await sleep(600);
  const q1 = readQueue();
  const ci1 = curInfo(q1);
  const position = q1.findIndex(x => wid(x) === movedQid);
  return JSON.stringify({
    ok: position >= 0,
    moved: true,
    noop: false,
    mergedCurrent: false,
    id: id,
    qid: movedQid,
    from: chosen,
    insertAt: at,
    anchor: anchor,
    position: position,
    currentIndex: ci1.index,
    currentId: ci1.id,
    currentChanged: (ci0.wrapper !== ci1.wrapper || ci0.start !== ci1.start || ci0.id !== ci1.id),
    queueLen: q1.length,
    error: position >= 0 ? null : 'moved_item_missing'
  });
})()