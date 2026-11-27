(async () => {
  const id = '__SONG_ID__';
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
  if (ci0.id === id) {
    return JSON.stringify({ok:false, error:'cannot_remove_current', id:id, currentIndex:ci0.index, currentId:ci0.id, queueLen:q0.length});
  }
  if (ci0.index < 0) {
    return JSON.stringify({ok:false, error:'current_not_found', id:id, currentIndex:ci0.index, currentId:ci0.id, queueLen:q0.length});
  }

  const pending = [];
  for (let i = ci0.index + 1; i < q0.length; i++) {
    if (idOf(q0[i]) === id) pending.push(i);
  }
  if (!pending.length) {
    return JSON.stringify({ok:true, removed:false, noop:true, id:id, removedCount:0, currentIndex:ci0.index, currentId:ci0.id, queueLen:q0.length, error:null});
  }

  const ids = [];
  q0.forEach((x, i) => {
    if (i > ci0.index && idOf(x) === id) return;
    ids.push(wid(x));
  });
  if (ids.some(x => !x)) {
    return JSON.stringify({ok:false, error:'empty_qid', id:id, removedCount:0, currentIndex:ci0.index, currentId:ci0.id, queueLen:q0.length});
  }

  try {
    m.r4('track_queue', ids);
  } catch (e) {
    return JSON.stringify({ok:false, error:'remove_failed', detail:String(e && (e.stack || e)), id:id, removedCount:0, currentIndex:ci0.index, currentId:ci0.id, queueLen:q0.length});
  }

  await sleep(600);
  const q1 = readQueue();
  const ci1 = curInfo(q1);
  let remaining = 0;
  for (let i = Math.max(0, ci1.index + 1); i < q1.length; i++) {
    if (idOf(q1[i]) === id) remaining++;
  }
  return JSON.stringify({
    ok: remaining === 0,
    removed: true,
    noop: false,
    id: id,
    removedCount: pending.length,
    position: -1,
    currentIndex: ci1.index,
    currentId: ci1.id,
    currentChanged: (ci0.wrapper !== ci1.wrapper || ci0.start !== ci1.start || ci0.id !== ci1.id),
    queueLen: q1.length,
    remaining: remaining,
    error: remaining === 0 ? null : 'remove_not_applied'
  });
})()
