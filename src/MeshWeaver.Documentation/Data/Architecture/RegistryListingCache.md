---
Name: The Registry Listing Cache
Category: Architecture
Description: "GET /api/plugins re-reads the whole plugins repository on every request and blew its 30 s attempt budget about 60 times a day. What is cached is the SOURCE snapshot, never the response — which is what keeps a latency fix from becoming a disclosure."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/></svg>
---

# The Registry Listing Cache

A plugin **registry** answers `GET /api/plugins` with the catalog an installation may install from.
Until #4222, every one of those requests read the whole source repository from GitHub and parsed
every manifest in it, and about sixty a day did not finish inside the client's 30-second attempt
budget.

## What was measured

| | |
|---|---|
| server side, 2026-08-26, from inside two production portals | `GET /api/plugins/bundles/index.json` — an 8.7 KB document — connects in ~0.03 s and takes **12–19 s to first byte** |
| consumer side, 2026-09-13, `Admin/_LogIncident/9ca334c1e8dad9ca` | **2,028** `plugin-registry` attempt timeouts over 33.6 days ⇒ **~60/day**, every sample `Standard-AttemptTimeout` |

Neither is an outage: the 90 s total budget covers a retry, so the listing usually lands on a later
attempt. What it costs is that **every catalog open and every `/Store` render can wait out a 30 s
attempt before anything is even retried**, and that sixty red lines a day have to be recognised and
discounted by every reader of the log.

## The mechanism

`GET /api/plugins` → `PluginRegistryEndpoints.List` does three things per request, and the first
dominates:

1. **`Sources(hub, config)` resolves the sources FRESH.** `PackageSources.FromConfiguration` →
   `PackageSources.FromRepo` constructs a **brand-new `IPackageSource` on every request**. That is the
   detail that decides the design: a cache held by the source instance would be discarded with it.
2. **`ListPackages(gitRef)` reads the repository.** For a URL source that is
   `IGitHubRepoClient.Fetch` — a shallow clone into a temp directory — followed by parsing every
   `package.json` / `index.json` in the tree. ~70 modules.
3. Then a mesh read of the pushed publications, and `IsGranted(caller, source, package)` per package.

## What is cached, and what is deliberately not

**The SOURCE snapshot, keyed by `(repoUrl, subdir, format, gitRef)` — never the response.**

🚨 **This is a security property, not an implementation detail.** The key carries **no caller**,
because the value carries **no per-caller decision**. The grant filter, the plan-tier refusals and
the pushed-artifact stamp all still run per request over the cached list. Caching the RESPONSE
instead would be #3768's shape — the registry answering with an identity that is not the caller's —
and a latency fix would have become a disclosure bug. `PackageListingCacheTest` pins it from the
other side: two instances with different grants, served off one cached listing, each see only their
own packages.

**File fetches are not cached.** `FetchPackageFiles` is a per-package read on the INSTALL path; it is
not repeated per catalog render, and a stale one would install stale bytes.

🚨 **A LOCAL directory source is not cached either, and that is a correctness boundary rather than a
tuning choice.** The defect is a repository fetched OVER THE NETWORK per request; a local read is
cheap, has no webhook to invalidate it, and its listed version is contractually a function of the
files on disk *right now* — a mounted source must report an edit on the very next listing
(`LocalSourceContentVersionTest` pins exactly that, and it is the test that caught the first version
of this change caching one). A local-dev or air-gapped registry would otherwise have reported
five-minute-old content.

**A fault is not cached.** The entry lives in a `PromiseCache`, which evicts on the terminal
`OnError`, so a transient GitHub failure is retried by the next caller rather than replayed for the
life of the pod (#1369). Concurrent first callers still share the one read — a burst of catalog opens
used to be a burst of clones, which is most of how the 30 s budget was reached at all.

## Invalidation, in two layers

**Primary — the green build.** The registry already receives the plugins repo's webhook; a green
build lands as `Admin/_Build/{owner}.{repo}`, and the listing for that repository is forgotten — so a
merge is visible on the next request rather than after a wait.

🚨 **Both source lists are covered, and they are genuinely different lists.** `PluginUpdateWatcher`
historically watched only repositories named by a **`PluginCatalog` NODE** (`SourceRepoPath`), while
`/api/plugins` serves what `PackageSources.FromConfiguration` reads from
**`PluginCatalog:Sources:N:RepoPath`** — which is how the fleet registry is wired. Evicting only on
the first would have left the freshness window as the *only* invalidation for the sources that
actually matter, silently. The watcher now also opens an **evict-only** build watch per configured
source, read through `FromConfiguration` itself so it cannot disagree with what the endpoint serves;
a repository that is on both lists is watched once and does both.

**Safety net — a freshness window.** `PluginCatalog:ListingCacheSeconds` (default **5 minutes**, `0`
switches caching off) bounds how stale a listing may be when a broadcast never arrives. This is the
same role, and the same justification, as `PluginCatalogOptions.ReconcileSafetyNetInterval` one layer
up — it bounds the staleness of a cache; it is not a bound placed over anything that hangs. A
malformed value falls back to the default rather than to "off": a typo must not silently restore the
per-request read.

## Where it lives

**A mesh-scoped singleton**, registered by `AddPluginCatalog` — never static
([No Static State](../NoStaticState)), so a test mesh cannot inherit another's catalog. It has to be
mesh-scoped for a second reason too: the thing that must outlive a request is precisely what the
per-request source factory cannot hold.

`PackageSources.FromRepo` wraps at **one** site, with the three source shapes built in a separate
`Build`, so a source shape added later is cached by construction rather than by whoever adds it
remembering. A mesh without the plugin catalog registered — a bare test mesh, the CLI — resolves no
cache and gets the source unchanged.

## The other half — what ONE read costs

A cache changes **how often** the repository is read. It does not change what a read costs, and the
first request after every eviction or window expiry still pays it in full, on a page-facing request.
So the same issue has a second half, and it is the one the 2026-08-26 comment did not name.

**The listing was transferring the whole repository to read its manifests.** `ListPackages` asked
for an unfiltered snapshot — `IGitHubRepoClient.Fetch(url, ref, subdir, token)` — which
`GitProtocolRepoClient` served as `git fetch --depth 1` plus a full checkout, and then it kept the
two files per plugin it parses and discarded everything else.

Measured against `MeshWeaver.Plugins` at `25dc26d7` on 2026-09-16:

| | files | bytes | wall clock |
|---|---|---|---|
| what the transfer moved | 4,995 | 47.8 MB compressed | **13.0 s** |
| what `ListPackages` reads (`<Plugin>/index.json` + `<Plugin>/manifest.lock`) | 143 | 0.8 MB | — |
| the narrow read, same commit, same answer | 143 | 1.3 MB | **3.3 s** |

Two readings matter in that table. The first is that **13 s lands exactly in the 12–19 s the
production portals measured**, which is what identifies the transfer as the dominant term rather
than anything the endpoint computes per request. The second is that the parse was never the problem:
reading all 4,995 files off a warm local disk takes **83 ms**, so a filter applied while reading the
worktree — which is what the client used to do — saves nothing a caller can feel.

**Nor was it entitlement.** The same comment blamed "entitlement per package and a mesh query on
every request". That is true of the sibling BUNDLE index, which really does run
`InstalledPackages` and `HeldPartitions` mesh queries per request — and false of this endpoint:
`IsGranted` is `caller.Allows(...)`, pure in-memory over a grant the authenticator already resolved,
and `Artifacts(hub)` is `NoPublicationArtifacts` on every deployment in the fleet, i.e.
`Observable.Return([])`. A measurement taken on one endpoint had been carried to another.

### How the narrow read works

`IGitHubRepoClient`'s filtered `Fetch` overload has always *stated* this contract — *"downloads ONLY
the blobs whose path satisfies `pathFilter` … without pulling the rest of the repo"* — and
`OctokitGitHubRepoClient` honoured it. `GitProtocolRepoClient` did not: it fetched everything and
filtered at read time, on the reasoning that the git protocol costs the same number of REST calls
(zero) either way. **True of calls, false of bytes.** And nothing called the overload, so the
listing never even asked.

The git-protocol client now selects before it transfers:

1. `git fetch --depth 1 --filter=blob:none` — a **blobless partial clone**. Commits and trees only,
   so the whole path list is known before one file's bytes move (1.5 s / 372 KB for the repo above).
2. `git ls-tree -r --name-only -z FETCH_HEAD` — that list, for free. `-z` because `ls-tree` quotes
   unusual paths otherwise, and a quoted path would be selected and read under a name the worktree
   does not have.
3. The caller's predicate selects from it, and the survivors become `--no-cone` **sparse-checkout**
   patterns, so the checkout materialises exactly those in **one** batched lazy fetch.

A filter that matches nothing skips the checkout entirely and moves no blobs at all. The worktree
read still applies the same predicate, so the ANSWER is identical however the transfer went.

🚨 **A remote that cannot serve partial clones does not fail — it warns and sends everything.**
`uploadpack.allowFilter` is a server capability; when it is off, git prints *"filtering not
recognized by server, ignoring"* and **exits 0** having transferred the whole pack. The answer stays
correct, but the saving is gone, and an unannounced loss of it is exactly how this latency would
return unnoticed. The client detects that line and says so in its log. Only a hard fetch failure
takes the fallback branch, and a failure of the fallback propagates — nothing is swallowed.

🚨 **Only the LISTING is narrowed.** `FetchPackageFiles` — the install path — still reads the whole
package folder; narrowing that one would install an empty package. The unfiltered `Fetch` is
untouched and still transfers everything, which is what a content sync wants.
`GitProtocolNarrowFetchTest` pins both halves, and pins **which** path ran rather than only what it
answered: a narrow fetch that silently regressed to a whole checkout still returns the right files,
so asserting the files alone would pass over the defect the test exists to prevent.

## Why not simply raise the client budget

The comment that already sits in `ServiceDefaults` says it, from 2026-08-26: *"a registry this slow
to serve 8.7 KB is its own defect and wants caching"*. The budget raise it documents was needed —
a client whose budget cannot cover the server's observed latency keeps failing after the server
improves — but the listing's inputs change at the cadence of a **merge**, not of a request, and
reading them per request is the defect.

## Where this sits

[Plugin Bundles in the Registry](../PluginBundlesInTheRegistry) — what the registry serves beside the
listing. [Controlled IO Pooling](../ControlledIoPooling) → "Promise-cache for idempotent one-shots" —
the `PromiseCache` contract this uses, including why a fault must evict. [No Static
State](../NoStaticState) — why the cache is an instance on a mesh-scoped singleton.
