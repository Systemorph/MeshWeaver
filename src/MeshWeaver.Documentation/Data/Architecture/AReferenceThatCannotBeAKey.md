---
Name: A Reference That Cannot Be a Key
Category: Architecture
Description: A live in-process census attributed every one of a production replica's 312 sync/ hubs to the stream that minted it, and split the duplicates into two named causes by comparing the reference objects already on the heap. Twenty-four were one value-identical pair whose reference has REFERENCE equality - a record with a collection member - so it could never hit the stream cache, and that group grew 18 to 24 while being watched. The census itself is committed here, because the last two versions of it were lost.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"/></svg>
---

# A Reference That Cannot Be a Key

[#3432](https://github.com/Systemorph/MeshWeaver/issues/3432) has carried the same title since
2026-09-06: *population MEASURED, cause NOT established*. This page closes what remained of the
cause, and it does so the way [The Read Path Minted a Hub Per Read](../ReadPathStreamMinting) did —
a live in-process census on a running production replica, not a heap dump and not a reading of
`src/`.

> **A `WorkspaceReference` IS a cache key. A positional `record` whose member is a collection does
> not get value equality for free — the compiler compares the collection BY REFERENCE — so such a
> reference can never hit `Workspace._localStreamCache`, and every read mints a permanent `sync/`
> hub on the owning node hub.**

Measured: **24 of them on one node hub, for one collection name — and still climbing, 18 → 24
in the 34 minutes between two readings.**

## 1. The reading that this issue always turned on

Every comment on #3432 from 2026-09-13 onwards was blocked on the same precondition: a replica
running a build that carries the fixes, and the census re-run on it. That precondition is now met.

**memex.systemorph.com, pod `memex-portal-deployment-7cb6684584-ztqz8`, pid 1, image
`afde4eabe0740ff658f3dae8a3073c33a2b3ea02`** — which carries `f41f8bda` (#3427),
[#3952](../ReadPathStreamMinting), `#4300` and `dbdaaacc3` ([#4163](../AHubThatPinsItsOwnCacheEntry)).
Five readings on the same pod and pid:

| UTC | uptime | hubs | `sync/` | working set | GC heap |
|---|---:|---:|---:|---:|---:|
| 14:31:41 | 825.3 min | 369 | 299 | 3 170 MiB | 879 MiB |
| 14:34:08 | 827.8 min | 381 | 309 | 3 158 MiB | 841 MiB |
| 14:36:57 | 830.6 min | 392 | 318 | 3 094 MiB | 945 MiB |
| 14:41:35 | 835.2 min | 384 | 311 | 3 085 MiB | 920 MiB |
| 15:15:57 | 869.6 min | 374 | 312 | 3 595 MiB | 1 139 MiB |

🚨 **The census is not a passive observer of its own subject.** Each run is an activity, and an
activity is a node with a hub: each reading's own activity accounts for 6 of the `sync/` hubs it
counts, and `rbuergi/Script/HubCensus7` grew from 7 to 11 across the runs. The population goes
**up and then down again** — 299 → 309 → 318 → 311 → 312 — which is what a bounded population under
a self-inflicted transient looks like, and is the opposite of the monotone climb the pre-fix
replicas showed (≈16 `sync/` hubs per minute, ≈985/h). **Do not read a slope off these rows; read
the boundedness — and then read §4, where ONE group inside this flat total is monotone.**

For scale, and stated as the non-comparison it is: the pre-fix readings in #3432 were taken on
**memex.meshweaver.cloud** — 8 420 `sync/` hubs at 26 h, 4 687 at 90 min, 8 595 at 55 min. This one
is a different instance with different load and **cannot be differenced against them**. What it can
establish, and does, is that on fixed code a replica 14.5 hours old holds 312.

## 2. What the census does that v3 and v6 could not

Both earlier instruments answered a question *about* the population. Neither could say, for a given
hub, **which stream minted it**.

- **v3** decomposed the population by holder (`_remoteStreamCache`, `_evictedRemoteStreams`,
  `_clientSubscriptions`, …) and left 36–38 % in a bucket called `residual`.
- **v6** classified every `sync/` hub by disposal state and established that none is wedged
  (`withBufferedMessage=0`, `executingATurn=0`, `snapshotUnreadable=0`), which ruled
  [#3593](https://github.com/Systemorph/MeshWeaver/issues/3593) out for this population.

**v7 attributes each hub to its minting stream, and the identity is exact rather than inferred.** A
`sync/` hub's address is `sync/{ClientId}` (`SynchronizationAddress.Create`), so the walk down the
hub's own `disposables` composite — where `SynchronizationStream`'s constructor registered
`syncHub.RegisterForDisposal(_ => ReleaseHub())`, the #3427 hook — accepts a candidate stream **only
when its `ClientId` equals this hub's `Address.Id`**. Anything else lands in its own printed bucket.

```
--- ATTRIBUTION: sync=312 attributed=312 noDisposablesField=0 emptyComposite=0
                 notFound=0 budgetExhausted=0 ---
   composite entry-count histogram: 4:280, 5:32
```

**312 of 312** on the final reading (and 318 of 318 on the 14:36:57 one), with every other outcome
reported at zero. That is the denominator the earlier `residual` numbers never had.

The v7.1 → v7.2 step is worth recording because the first version of the walk read
`attributed=35 noCandidate=156 noIdentityMatch=108` — it collected up to 32 streams per hub and
then looked for a match, and on most hubs the budget was spent before the right one was reached.
**35 of 299 attributed is an instrument failure, not a finding**, and it is only visible because
those two buckets are printed. A targeted walk — stop at the first `ClientId` match, under an
explicit visit budget whose exhaustion is its own bucket — reads every hub on every run since.

### What the whole population is made of

```
(15:15:57 reading)
--- by REFERENCE type (what was reduced) ---   --- by HOST hub type (whose lifetime pays) ---
   114  MeshNodeReference                          44  rbuergi             20  Admin
    62  CollectionsReference                       39  portal              17  DeepSign
    46  CollectionReference                        28  Doc                 16  Hosting
    30  EntityReference                            28  cache               15  Edu
    24  ContentCollectionReference                 24  CollaborationNotus  13  Approvals
    23  LayoutAreaReference                        22  rsalzmann           13  PartnerRe
    12  JsonPointerReference
     1  SchemaReference
```

The dominant shape is a node hub's own data-source reduces — `MeshNodeReference{Path=X}`,
`CollectionReference /MeshNode` and `CollectionsReference MeshNode` on `host=X`, `owner=ds/X`. That
is **bounded per node hub and retires with it**, and it is precisely the "6–7 per parent the census
could not attribute" that #3432's 2026-09-11 comment called the residual.

## 3. The discriminator: duplicates of an identical (host, reference) pair

A cached reduce yields exactly ONE stream per `(host, reference)`. A second stream carrying the
**same pair** can only have come from a call the cache did not serve. So the excess over the
distinct-pair count is the per-call minting, and it needs no reasoning about call sites:

```
--- DUPLICATE MINTS: streams=312 distinctPairs=232 excess=80 ---
```

🚨 **The first version of this reading was computed with a TRUNCATED grouping key** — the pair key
kept only the first 120 characters of the reference — and it read `streams=311 distinctPairs=248
excess=63`. Truncation MERGES two distinct long references that share a host, owner and prefix into
one bucket and reports them as a duplicate: a false positive in exactly the direction the reading is
used to argue. The figures above are the re-run with the full reference as the key, and the script
in §9 is the corrected one. **Truncate what is displayed, never what is compared.**

80 of 312 — 26 % of the live population — are duplicate mints. Two different things can produce
one, and **they call for opposite responses**:

- the caller took the **deliberately uncached configured branch** (`Workspace.GetStream(reference,
  configuration)`), which is correct by contract and retires with the subscriber; or
- the reference **cannot be a key at all**, so the cache misses on every call, for ever.

## 4. Separating them without constructing anything

The two hypotheses are distinguished by asking the heap. For each duplicate group the census
compares the **live reference objects it already holds** — no new objects, no calls into the
workspace, no side effect of any kind:

```
   x24   [ContentCollectionReference] refsEqual=NO  -> the reference CANNOT be a cache key (identity equality)
          host=Doc/Architecture owner=Doc/Architecture ref=collection/content
   x5    [MeshNodeReference]     refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
   x4    [MeshNodeReference]     refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
   x3    [CollectionsReference]  refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
   x3    [JsonPointerReference]  refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
   …
   x2    [EntityReference]       refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
   x2    [LayoutAreaReference]   refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)
```

🚨 **This is the falsifying case, and it carries its own control.** Every other duplicate group reads
`refsEqual=YES` — `MeshNodeReference`, `CollectionsReference`, `JsonPointerReference`,
`EntityReference` and `LayoutAreaReference`, the last of which overrides its own equality precisely
so it can be compared. **Exactly one type reads `NO`.** Had the equality hypothesis been wrong,
`ContentCollectionReference` would have read `YES` with the rest; had the probe been vacuous — an
`Equals` that is always false — every group would have read `NO`. Neither happened.

So the 80 split cleanly:

| | streams | excess | what it is |
|---|---:|---:|---|
| `ContentCollectionReference` on `Doc/Architecture` | 24 | **23** | **a defect** — the key is unhittable |
| everything else | 288 | 57 | the configured branch, by contract, retires with its subscriber |

🚨 **And that one group is MONOTONE, which the rest are not.** Across the two readings 34 minutes
apart on the same pod and pid it went **18 → 24** for the same single reference on the same node hub,
while the whole `sync/` population moved 311 → 312. The defect is not a high-water mark left over
from a burst; it mints on every read, for ever, for as long as that node hub lives.

## 5. The defect

```csharp
// src/MeshWeaver.ContentCollections/ContentCollectionReference.cs — as it stood
public record ContentCollectionReference(params IReadOnlyCollection<string>? CollectionNames)
    : WorkspaceReference<object>;
```

The compiler-generated `Equals`/`GetHashCode` go through
`EqualityComparer<IReadOnlyCollection<string>>.Default`, which for a `string[]` is **reference
equality**. Two `new ContentCollectionReference(["content"])` are therefore never `Equals` and never
share a hash. `Workspace._localStreamCache` is a
`ConcurrentDictionary<(WorkspaceReference, bool), Lazy<ISynchronizationStream>>`, so `GetOrAdd`
misses on every call — and every miss constructs a `SynchronizationStream`, hence a hosted
`sync/{id}` sub-hub with its own Autofac scope, `TypeRegistry` and `JsonSerializerOptions` (~390 KB),
registered for disposal on the **hub-lifetime** node hub.

This is [#3952](../ReadPathStreamMinting)'s defect reached by the other door. There the cache was
**bypassed** (a constant configuration took the uncached branch); here it is **unhittable**. The
remedy is the same shape and the same size: one reference, one stream.

🚨 **The codebase already knew.** Three types carry hand-written `Equals`/`GetHashCode` for exactly
this reason, and say so:

| type | why it overrides |
|---|---|
| `CollectionsReference` | *"Determines equality by collection-name sequence"* — the same shape, in the same role |
| `LayoutAreaReference` | *"Override the generated Equals and GetHashCode to exclude the parameters field"* — a `Lazy<>` member |
| `Address` | *"Two addresses are equal when their segments match in order"* — a `string[]` member |

`ContentCollectionReference` is `CollectionsReference`'s sibling written without them, in a
different assembly, later. `AggregateWorkspaceReference` and `CombinedStreamReference` are the other
two; neither is used as a cache key today, so neither was measured, but both are the same trap
waiting.

## 6. The fix, and the guard that makes it stick

The three references get the overrides their siblings already had. The durable half is the guard:

**`EveryWorkspaceReferenceWithAnIdentityComparedMember_DeclaresValueEquality`** scans every
non-abstract `WorkspaceReference` in `MeshWeaver.Data.Contract` and `MeshWeaver.ContentCollections`,
flags each one whose instance fields include a type the compiler compares by reference (an array, a
non-string `IEnumerable`, or a `Lazy<>`), and requires a **hand-written** `Equals(T)` and
`GetHashCode()`.

🚨 **The first version of that guard could not fail, and the negative control is what caught it.**
It asked whether the type *declares* `Equals(T)` — which a record ALWAYS does, because the compiler
synthesises it — so it went green on the unfixed build having checked nothing. What separates a
hand-written override from the synthesised one is `[CompilerGenerated]`. The guard now asserts its
own detector in **both** directions before using it: a probe record with an array member and no
overrides must be flagged, and `CollectionsReference` must not be.

| arm | unfixed | fixed |
|---|---|---|
| `TwoValueIdenticalReferences_AreEqualAndShareAHash` | **fails** | passes |
| `ReadingTheSameCollectionReference_DoesNotMintASyncHubPerRead` | **fails** (`+5` for 5 reads) | passes (`+0`) |
| `EveryWorkspaceReferenceWithAnIdentityComparedMember_DeclaresValueEquality` | **fails**, naming the type | passes (`scanned=20 needOverride=5`) |
| `AConfiguredReduce_StillMintsOneSyncHubPerCall` | passes (`+5`) | passes (`+5`) |

The last arm is the control in the growing direction: without it, "the count did not move" would
also pass on a build where the counter is blind.

## 7. The census, conserved

🚨 **This section exists because the instrument has now been lost twice.** #3432's 2026-09-14 comment
went looking for the v6 script and found only v3 — *"most likely it was an unsaved edit executed
transiently"* — and re-deriving it was a named blocker on the closing reading for two more days.
A `Code` node in an author's own partition is not a durable form.

The runnable copy lives at `rbuergi/Script/HubCensus7` on the control instance, which is a
scratch node in one person's partition and will go the way of the last two. **The durable copy is
the listing at the end of this page** — paste it into an executable `Code` node and run it. The
recipe it implements is:

1. Climb `Configuration.ParentHub` to the mesh root; recurse `MessageHub.hostedHubs`, recording each
   hub's parent. `Address.Type == "sync"` selects the population.
2. Cross-tab it by `RunLevel` × `disposalStarted`, and print `snapshotUnreadable` — the denominator.
3. For each `sync/` hub, walk its `disposables` composite to the stream whose `ClientId` equals the
   hub's `Address.Id`. Print `noDisposablesField`, `emptyComposite`, `notFound` and
   `budgetExhausted` separately, so an unattributed hub can never read as an attributed one.
4. Group by `(host, reference)`; the excess over the distinct-pair count is the duplicate minting.
5. For each duplicate group, compare the **live** reference objects: `refsEqual=NO` means the
   reference cannot be a key; `YES` means the cache was bypassed.

🚨 **It resolves nothing from DI.** `IWorkspace` is `AddScoped`, so
`hub.ServiceProvider.GetService<IWorkspace>()` on a hub that never built one **constructs** one — a
census that creates the thing it counts. Every read in the walk is a field read on an object already
reachable from the hub tree. (v7.0 did resolve it, and that is why the version committed here does
not.)

🚨 **And it is the wrong instrument for a heap dump's question.** [Portal Heap Is
Hubs](../PortalHeapIsHubs) measured what `dotnet-dump collect --type Heap` costs on a replica fat
enough to be interesting: a 106 s thread suspension against a 90 s liveness budget, i.e. the replica
restarts and the state being measured is destroyed. The census runs in ~10 ms.

## 8. What this does NOT claim

- **Not that the population is now bounded in general.** 57 of the 80 duplicate mints are the
  configured branch. They are correct by contract and retire with their subscriber, but nothing here
  measured that they DO retire — only that they are not the equality defect.
- **Not a slope.** Five readings minutes apart, each of which the census itself perturbed, measure
  boundedness, not a rate. A rate needs readings hours apart on an unperturbed pod.
- **Not a like-for-like comparison with #3432's own baselines**, which were taken on
  memex.meshweaver.cloud — still on `c84c6c05`, still unrolled. The comparative reading this thread
  has wanted since 2026-09-13 still needs that instance rolled.
- **Not that `Doc/Architecture` was the only victim.** 24 is what one node hub on one replica had
  accumulated; the defect is in the reference type, so its cost is per content-enabled node hub and
  per read.

## 9. The census, in full

Paste into an executable `Code` node (`nodeType: Code`, `isExecutable: true`) and run it. It is
read-only, bounded, and completes in about 10 ms on a 400-hub replica. The header it prints —
`pod`, `pid`, `upMin`, `wsMiB`, `gcMiB`, `gen2` — is what makes two readings comparable: **a
differing pod or pid is a different population, not a trend.**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MeshWeaver.Messaging;
using MeshWeaver.Data;

// CENSUS7.2 (#3432) — READ-ONLY, in-process, and it RESOLVES NOTHING FROM DI.
//
// Answers the one question v3 (holder decomposition) and v6 (disposal cross-tab) could not:
// for EVERY sync/ hub, WHICH STREAM MINTED IT, named by the Reference that was reduced and the
// host the reduce was registered on. Identity is EXACT, never inferred: a sync hub's address is
// sync/{ClientId} (SynchronizationAddress.Create), so a candidate counts only when its ClientId
// equals this hub's Address.Id. Every other outcome is reported in its own bucket, so the
// denominator is whole and an unattributed hub can never read as an attributed one.
//
// 🚨 Deliberately does NOT call ServiceProvider.GetService<IWorkspace>(): IWorkspace is AddScoped,
// so resolving it on a hub that never built one CONSTRUCTS one — a census that creates the thing
// it counts. Every read below is a field read on an object already reachable from the hub tree.

const BindingFlags IB = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

object? Fld(object? o, string name)
{
    if (o is null) return null;
    for (var t = o.GetType(); t is not null; t = t.BaseType)
    {
        var f = t.GetField(name, IB);
        if (f is not null) { try { return f.GetValue(o); } catch { return null; } }
    }
    return null;
}

object? Prop(object? o, string name)
{
    if (o is null) return null;
    for (var t = o.GetType(); t is not null; t = t.BaseType)
    {
        var p = t.GetProperty(name, IB);
        if (p is not null && p.GetIndexParameters().Length == 0) { try { return p.GetValue(o); } catch { return null; } }
    }
    return null;
}

var proc = Process.GetCurrentProcess();
var upMin = (DateTime.Now - proc.StartTime).TotalMinutes;
Console.WriteLine($"CENSUS7.2 pod={Environment.MachineName} pid={proc.Id} upMin={upMin:F1} " +
                  $"wsMiB={proc.WorkingSet64 / 1048576} gcMiB={GC.GetTotalMemory(false) / 1048576} " +
                  $"gen2={GC.CollectionCount(2)} utc={DateTime.UtcNow:O}");

IMessageHub root = Mesh;
for (var i = 0; i < 64; i++)
{
    IMessageHub? parent = null;
    try { parent = root.Configuration?.ParentHub; } catch { }
    if (parent is null || ReferenceEquals(parent, root)) break;
    root = parent;
}
Console.WriteLine($"root={root.Address}");

var allHubs = new List<IMessageHub>();
var parentOf = new Dictionary<string, string>();
var walkErrors = 0;

void Walk(IMessageHub h, IMessageHub? parent, int depth)
{
    allHubs.Add(h);
    try { parentOf[h.Address.ToString()] = parent?.Address.ToString() ?? "<root>"; } catch { }
    if (depth >= 20) return;
    IMessageHub[]? children = null;
    try { children = (Prop(Fld(h, "hostedHubs"), "Hubs") as IEnumerable)?.Cast<IMessageHub>().ToArray(); }
    catch { walkErrors++; }
    if (children is null) return;
    foreach (var c in children) { try { Walk(c, h, depth + 1); } catch { walkErrors++; } }
}
Walk(root, null, 0);

var syncHubs = allHubs.Where(h => { try { return h.Address?.Type == "sync"; } catch { return false; } }).ToArray();
Console.WriteLine($"hubs={allHubs.Count} sync={syncHubs.Length} nonSync={allHubs.Count - syncHubs.Length} walkErrors={walkErrors}");

var unreadable = 0;
var crossTab = new Dictionary<string, int>();
foreach (var h in syncHubs)
{
    try
    {
        var rl = Prop(h, "RunLevel")?.ToString() ?? "?";
        var disp = Fld(h, "disposalStarted") is bool b && b ? "DisposalStarted" : "<not started>";
        var key = rl + " / " + disp;
        crossTab[key] = crossTab.TryGetValue(key, out var n) ? n + 1 : 1;
    }
    catch { unreadable++; }
}
foreach (var kv in crossTab.OrderByDescending(k => k.Value))
    Console.WriteLine($"   {kv.Value,6}  {kv.Key}");
Console.WriteLine($"   snapshotUnreadable={unreadable}");

// ---------- ATTRIBUTION ----------
// Targeted: stop at the FIRST stream whose ClientId matches this hub's Address.Id. A shared
// visit budget bounds the walk; exhausting it is its own bucket, never a silent miss.
var budget = 0;

ISynchronizationStream? FindMine(object? o, int depth, HashSet<object> seen, string clientId)
{
    if (o is null || depth > 9 || budget <= 0) return null;
    budget--;
    if (o is ISynchronizationStream s)
    {
        try { if (string.Equals(s.ClientId, clientId, StringComparison.Ordinal)) return s; } catch { }
        return null;   // a stream that is not ours is a dead end, not a candidate
    }
    var t = o.GetType();
    if (t.IsPrimitive || o is string || o is Address || o is Type) return null;
    if (!seen.Add(o)) return null;

    if (o is Delegate d) return FindMine(d.Target, depth + 1, seen, clientId);

    if (o is IEnumerable en)
    {
        var i = 0;
        try
        {
            foreach (var item in en)
            {
                if (i++ > 256 || budget <= 0) break;
                var r = FindMine(item, depth + 1, seen, clientId);
                if (r is not null) return r;
            }
        }
        catch { }
        return null;
    }

    foreach (var f in t.GetFields(IB))
    {
        if (f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
        if (budget <= 0) return null;
        object? v;
        try { v = f.GetValue(o); } catch { continue; }
        var r = FindMine(v, depth + 1, seen, clientId);
        if (r is not null) return r;
    }
    return null;
}

var byReference = new Dictionary<string, int>();
var byHost = new Dictionary<string, int>();
var byRefAndHost = new Dictionary<string, int>();
var examples = new Dictionary<string, string>();
var noDisposables = 0;
var emptyDisposables = 0;
var notFound = 0;
var budgetExhausted = 0;
var entryHisto = new Dictionary<int, int>();
var pairs = new List<string>();
var liveRefs = new Dictionary<string, List<object>>();

foreach (var h in syncHubs)
{
    string clientId;
    try { clientId = h.Address.Id; } catch { notFound++; continue; }

    var comp = Fld(h, "disposables");
    if (comp is null) { noDisposables++; continue; }
    var entryCount = -1;
    try { entryCount = (comp as IEnumerable)?.Cast<object>().Count() ?? -1; } catch { }
    entryHisto[entryCount] = entryHisto.TryGetValue(entryCount, out var eh) ? eh + 1 : 1;
    if (entryCount == 0) { emptyDisposables++; continue; }

    budget = 20000;
    ISynchronizationStream? mine = null;
    try { mine = FindMine(comp, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), clientId); }
    catch { }
    if (mine is null)
    {
        if (budget <= 0) budgetExhausted++; else notFound++;
        continue;
    }

    string refType, refTypeId, refStr, hostAddr, owner;
    // 🚨 TWO names, on purpose. `Name` is for DISPLAY; it is NOT a type identity — two
    // WorkspaceReference implementations in different namespaces or assemblies share it, and here
    // that is routine rather than exotic (every NodeType recompile mints a same-named type in a new
    // collectible assembly). The KEY below uses the assembly-qualified name, so same-named types
    // from different assemblies stay different keys.
    try { refType = mine.Reference?.GetType().Name ?? "null"; } catch { refType = "?"; }
    try { refTypeId = mine.Reference?.GetType().AssemblyQualifiedName ?? "null"; } catch { refTypeId = "?"; }
    try { refStr = mine.Reference?.ToString() ?? ""; } catch { refStr = "?"; }
    try { hostAddr = (Prop(mine, "Host") as IMessageHub)?.Address?.ToString() ?? "?"; } catch { hostAddr = "?"; }
    try { owner = mine.Owner?.ToString() ?? "?"; } catch { owner = "?"; }

    var hostType = hostAddr.Split('/')[0];
    byReference[refType] = byReference.TryGetValue(refType, out var a) ? a + 1 : 1;
    byHost[hostType] = byHost.TryGetValue(hostType, out var b2) ? b2 + 1 : 1;
    var k = $"{refType,-26} host={hostType}";
    byRefAndHost[k] = byRefAndHost.TryGetValue(k, out var c3) ? c3 + 1 : 1;
    if (!examples.ContainsKey(k))
        examples[k] = $"host={hostAddr} owner={owner} ref={(refStr.Length > 160 ? refStr[..160] : refStr)}";
    // 🚨 THE KEY CARRIES THE TYPE IDENTITY AND THE FULL RENDERING. Three mistakes here each
    // manufacture false duplicates, in exactly the direction this reading is used to argue:
    //   · TRUNCATING merges two distinct long references sharing a host, owner and prefix;
    //   · dropping the TYPE merges references of DIFFERENT types that render alike
    //     (`CollectionReference("x")` and a `JsonPointerReference` can both print `/x`);
    //   · using the type's SHORT NAME merges same-named types from different assemblies, which
    //     this mesh mints routinely — one per NodeType recompile.
    // Truncate only what is DISPLAYED (`examples` above); never what is COMPARED.
    var pairKey = $"host={hostAddr} owner={owner} type={refTypeId} ref={refStr}";
    pairs.Add(pairKey);
    if (!liveRefs.TryGetValue(pairKey, out var bucket)) { bucket = new List<object>(); liveRefs[pairKey] = bucket; }
    if (mine.Reference is not null) bucket.Add(mine.Reference);
}

var attributed = byReference.Values.Sum();
Console.WriteLine($"--- ATTRIBUTION: sync={syncHubs.Length} attributed={attributed} " +
                  $"noDisposablesField={noDisposables} emptyComposite={emptyDisposables} " +
                  $"notFound={notFound} budgetExhausted={budgetExhausted} ---");
Console.WriteLine("   composite entry-count histogram: " +
                  string.Join(", ", entryHisto.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")));

Console.WriteLine("--- by REFERENCE type (what was reduced) ---");
foreach (var kv in byReference.OrderByDescending(k => k.Value))
    Console.WriteLine($"   {kv.Value,6}  {kv.Key}");

Console.WriteLine("--- by HOST hub type (whose lifetime pays for it) ---");
foreach (var kv in byHost.OrderByDescending(k => k.Value).Take(20))
    Console.WriteLine($"   {kv.Value,6}  {kv.Key}");

Console.WriteLine("--- by REFERENCE x HOST ---");
foreach (var kv in byRefAndHost.OrderByDescending(k => k.Value).Take(25))
    Console.WriteLine($"   {kv.Value,6}  {kv.Key}\n            e.g. {examples[kv.Key]}");

// ---------- THE DISCRIMINATOR: duplicate mints of an IDENTICAL (host, reference) pair ----------
// A cached reduce yields exactly ONE stream per (host, reference). A second stream carrying the
// same pair can only have come from an UNCACHED mint, so the excess over the distinct-pair count
// IS the per-call minting this issue is about. Zero excess falsifies the per-read-minting framing;
// a large excess names the site.
var pairCount = new Dictionary<string, int>();
foreach (var kv in pairs)
    pairCount[kv] = pairCount.TryGetValue(kv, out var pc) ? pc + 1 : 1;
var distinctPairs = pairCount.Count;
var excess = pairs.Count - distinctPairs;
Console.WriteLine($"--- DUPLICATE MINTS: streams={pairs.Count} distinctPairs={distinctPairs} excess={excess} ---");
// 🚨 THE TWO HYPOTHESES, separated with no side effect at all. A duplicate pair means the cache
// did not serve the second call. Either (a) the reference cannot be a KEY — two value-identical
// references are not Equals, so ConcurrentDictionary.GetOrAdd misses every time — or (b) the
// caller took the deliberately-uncached configured branch. Comparing the LIVE reference objects
// that are already on the heap decides it, and constructs nothing.
foreach (var kv in pairCount.Where(k => k.Value > 1).OrderByDescending(k => k.Value).Take(25))
{
    var verdict = "?";
    var typeName = "?";
    if (liveRefs.TryGetValue(kv.Key, out var refs) && refs.Count > 1)
    {
        typeName = refs[0].GetType().Name;
        var allEqual = true;
        var allSameHash = true;
        for (var i = 1; i < refs.Count; i++)
        {
            try { if (!refs[0].Equals(refs[i])) allEqual = false; } catch { allEqual = false; }
            try { if (refs[0].GetHashCode() != refs[i].GetHashCode()) allSameHash = false; } catch { allSameHash = false; }
        }
        verdict = allEqual
            ? (allSameHash ? "refsEqual=YES hash=SAME -> cache BYPASSED (configured branch)"
                           : "refsEqual=YES hash=DIFFERENT -> broken GetHashCode")
            : "refsEqual=NO -> the reference CANNOT be a cache key (identity equality)";
    }
    Console.WriteLine($"   x{kv.Value,-4} [{typeName}] {verdict}");
    Console.WriteLine($"          {kv.Key}");
}

Console.WriteLine("--- top parent hubs by hosted sync/ hubs ---");
var byParent = syncHubs
    .GroupBy(h => { try { return parentOf.TryGetValue(h.Address.ToString(), out var p) ? p : "?"; } catch { return "?"; } })
    .OrderByDescending(g => g.Count()).Take(20);
foreach (var g in byParent) Console.WriteLine($"   {g.Count(),6}  {g.Key}");
Console.WriteLine("CENSUS7.2 END");
```

## Related

- [The Read Path Minted a Hub Per Read](../ReadPathStreamMinting) — the same defect through the
  other door: the cache bypassed rather than unhittable, and the census this one descends from
- [A Hub That Pins Its Own Cache Entry](../AHubThatPinsItsOwnCacheEntry) — #4163, the retainer the
  measured replica carries
- [The sync/ Hub Population](../SyncHubPopulation) — the 1:1 stream ↔ hub identity all of this rests on
- [The Evicted-Stream Retention](../EvictedStreamRetention) — the mechanism §3 of ReadPathStreamMinting re-scoped to 1 of 475
- [Portal Heap Is Hubs](../PortalHeapIsHubs) — the dumps, and why the dump is the wrong instrument
