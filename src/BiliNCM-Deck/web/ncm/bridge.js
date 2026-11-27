(() => {
  const portal = document.querySelector('#portal_root');
  const rootEl = document.querySelector('#root');
  const realRoot = portal && portal._reactRootContainer && portal._reactRootContainer._internalRoot;
  if (realRoot) {
    window._fiberRoot = realRoot;
    if (rootEl && portal._reactRootContainer) {
      rootEl._reactRootContainer = portal._reactRootContainer;
    }
  }
  function ensureStore() {
    if (window._reduxStore) return true;
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
  let ok = false;
  let error = null;
  try { ok = ensureStore(); } catch (e) { error = String(e && (e.stack || e)); }
  return JSON.stringify({
    ok: ok,
    href: location.href,
    hasPortalRoot: !!portal,
    hasReactRoot: !!realRoot,
    hasStore: !!window._reduxStore,
    error: error
  });
})()