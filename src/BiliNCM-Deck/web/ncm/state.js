(() => {
  function ensureStore() {
    if (window._reduxStore) return true;
    const portal = document.querySelector('#portal_root');
    const realRoot = portal && portal._reactRootContainer && portal._reactRootContainer._internalRoot;
    if (realRoot) window._fiberRoot = realRoot;
    const root = window._fiberRoot || realRoot;
    if (!root) return false;
    let queue = [root.current || root];
    while (queue.length > 0) {
      const node = queue.shift();
      if (!node) continue;
      if (node.memoizedProps && node.memoizedProps.store) { window._reduxStore = node.memoizedProps.store; return true; }
      if (node.stateNode && node.stateNode.store) { window._reduxStore = node.stateNode.store; return true; }
      let child = node.child;
      while (child) { queue.push(child); child = child.sibling; }
    }
    return false;
  }
  if (!ensureStore()) return JSON.stringify({ok:false, error:'no_store'});
  const st = window._reduxStore.getState();
  const m = window.ctl && window.ctl.defPlayer && window.ctl.defPlayer.MF;
  const read = (k) => {
    try {
      if (!m || typeof m.V !== 'function') return [];
      const a = m.V(k);
      return Array.isArray(a) ? a : [];
    } catch (e) { return []; }
  };
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
  const trackOf = (x) => (x && (x.track || x)) || null;
  const nameOf = (x) => { const t = trackOf(x); return (t && (t.name || t.songName)) || ''; };
  const artistsOf = (x) => {
    const t = trackOf(x);
    const a = (t && (t.artists || t.ar)) || [];
    return a.map(v => v && (v.name || v.nickname)).filter(Boolean).join('/');
  };
  const pack = (x) => {
    const id = idOf(x);
    if (!id) return null;
    return { id: id, qid: qidOf(x), name: nameOf(x), artists: artistsOf(x) };
  };
  const queue = read('track_queue').map(pack).filter(Boolean);
  const cplRaw = (st.playingList && st.playingList.curPlayingList) || [];
  const cpl = cplRaw.map(pack).filter(Boolean);

  let curId = '';
  let curWrapper = '';
  let curStart = '';
  let curState = null;
  let curName = '';
  let curArtists = '';
  let curDuration = 0;
  let curPosition = null;
  try {
    const u = m && typeof m.U === 'function' ? m.U() : null;
    if (u && u.data && u.data.id != null) {
      curId = String(u.data.id);
      curName = u.data.name || '';
      if (Array.isArray(u.data.artists)) {
        curArtists = u.data.artists.map(a => a && a.name).filter(Boolean).join('/');
      }
      const durationValue = u.data.dt != null ? u.data.dt : u.data.duration;
      const durationNumber = Number(durationValue);
      if (Number.isFinite(durationNumber) && durationNumber > 0) curDuration = durationNumber;
      if (u.from) {
        if (u.from.id != null) curWrapper = String(u.from.id);
        if (u.from.startlogtime != null) curStart = String(u.from.startlogtime);
      }
      curState = u.state;
      const positionCandidates = [
        u.position, u.currentTime, u.playedTime, u.progress,
        u.data.position, u.data.currentTime, u.data.playedTime, u.data.progress,
        m && m.position, m && m.currentTime, m && m.playedTime, m && m.progress
      ];
      for (let i = 0; i < positionCandidates.length; i++) {
        const positionNumber = Number(positionCandidates[i]);
        if (Number.isFinite(positionNumber) && positionNumber >= 0) {
          curPosition = positionNumber;
          break;
        }
      }
      if (curPosition == null) {
        try {
          const audio = document.querySelector('audio');
          if (audio && Number.isFinite(Number(audio.currentTime))) curPosition = Number(audio.currentTime);
        } catch (e) { }
      }
    }
  } catch (e) { }
  const stId = st.playing && (st.playing.resourceTrackId || st.playing.onlineResourceId);
  if (!curId && stId != null) curId = String(stId);

  let idx = curWrapper ? queue.findIndex(x => x.qid === curWrapper) : -1;
  let matchedBy = idx >= 0 ? 'wrapper' : '';
  if (idx < 0 && curId) {
    idx = queue.findIndex(x => x.id === curId);
    if (idx >= 0) matchedBy = 'tid';
  }
  const entry = idx >= 0 ? queue[idx] : (cpl.find(x => x.id === curId) || null);
  if (!curName && entry) curName = entry.name;
  if (!curArtists && entry) curArtists = entry.artists;
  const current = curId ? {
    id: curId,
    qid: curWrapper,
    name: curName,
    artists: curArtists,
    start: curStart,
    state: curState,
    duration: curDuration,
    position: curPosition
  } : null;
  return JSON.stringify({
    ok: true,
    page: String(performance.timeOrigin || 0),
    current: current,
    currentIndex: idx,
    currentMatchedBy: matchedBy,
    currentWrapper: curWrapper,
    queue: queue,
    playingList: cpl,
    queueLen: queue.length
  });
})()