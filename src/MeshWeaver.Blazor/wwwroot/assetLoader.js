// Once-only loader for view-pack static assets (classic scripts + stylesheets that cannot ride
// Blazor's per-component <HeadContent> — e.g. a third-party library script components invoke via
// JS interop). Memoized per document: repeated ensure() calls for the same URL await the same
// load, so every pack view can call it unconditionally on first render. This is what keeps pack
// assets OUT of App.razor — the shell stays pack-free and a pack is droppable without touching it.
const loads = new Map();

export function ensure(url, kind) {
  let p = loads.get(url);
  if (p) return p;
  // The asset may already be in the document from OUTSIDE this module — a host page tag or an
  // earlier non-module load. Appending again would double-execute a script, so detect by the
  // element's RESOLVED url (el.src / el.href are absolute; the caller's url may be relative).
  const absolute = new URL(url, document.baseURI).href;
  const existing =
    kind === "css"
      ? [...document.querySelectorAll("link[rel='stylesheet']")].some((el) => el.href === absolute)
      : [...document.querySelectorAll("script[src]")].some((el) => el.src === absolute);
  if (existing) {
    p = Promise.resolve(true);
    loads.set(url, p);
    return p;
  }
  p = new Promise((resolve, reject) => {
    let el;
    if (kind === "css") {
      el = document.createElement("link");
      el.rel = "stylesheet";
      el.href = url;
    } else if (kind === "js") {
      el = document.createElement("script");
      el.src = url;
    } else {
      reject(new Error("assetLoader: unknown kind '" + kind + "' for " + url + " — use 'css' or 'js'"));
      return;
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
