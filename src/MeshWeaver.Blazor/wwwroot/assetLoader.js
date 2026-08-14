// Once-only loader for view-pack static assets (classic scripts + stylesheets that cannot ride
// Blazor's per-component <HeadContent> — e.g. a third-party library script components invoke via
// JS interop). Memoized per document: repeated ensure() calls for the same URL await the same
// load, so every pack view can call it unconditionally on first render. This is what keeps pack
// assets OUT of App.razor — the shell stays pack-free and a pack is droppable without touching it.
const loads = new Map();

export function ensure(url, kind) {
  let p = loads.get(url);
  if (p) return p;
  p = new Promise((resolve, reject) => {
    let el;
    if (kind === "css") {
      el = document.createElement("link");
      el.rel = "stylesheet";
      el.href = url;
    } else {
      el = document.createElement("script");
      el.src = url;
    }
    el.onload = () => resolve(true);
    el.onerror = () => {
      // Evict the failed promise so a later render can retry (transient network blip);
      // memoizing a failure would leave every future pack view permanently asset-less.
      loads.delete(url);
      reject(new Error("assetLoader: failed to load " + url));
    };
    document.head.appendChild(el);
  });
  loads.set(url, p);
  return p;
}
