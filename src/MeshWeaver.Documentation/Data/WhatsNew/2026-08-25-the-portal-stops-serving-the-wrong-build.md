---
Name: The portal stops serving the wrong build
Category: Fix
Description: The assembly cache no longer keeps every recompile forever (the write that adds a version now trims the type's directory), boot says out loud which copy of each module pack it loaded, and a replica that quietly bloats while still answering its probes now raises an alert.
Icon: ArrowSyncCheckmark
Order: -20260825
---

# The portal stops serving the wrong build

Three failures with one shape: work that had definitely shipped was not what the portal was running,
and nothing said so. A recompile that could not write, a fix that was loaded from the wrong file, and
a pod that was Ready while it was not working — each of them looked green from every angle except
production.

## Recompiles no longer fill the disk

On 2026-08-22 the shared 16 GiB `/data` volume hit 100% and **every** NodeType recompile started
failing with `No space left on device`. What you saw was `compilationStatus: Error` on a type node —
four steps from the cause — while already-compiled types kept serving happily.

The cache was keeping every compile it had ever done. One type's directory alone held **4,184**
dll/pdb files, one pair per recompile since June, and — the part that mattered — all of them
belonged to a *single* framework generation, which is the axis the existing generation-based
retention buckets by. Three generations of that shape is still ~12,500 files, so no setting of the
existing knob could have helped.

Now the write that adds a version trims the directory it just grew: the newest **3** versions of that
type stay, older ones go. The pass that knows the directory got bigger is the one that cleans it, so
nothing has to walk a share of thousands of files to discover the growth.

It is deliberately conservative about what it touches. It never crosses a framework generation — those
bytes can belong to an image another pod is still running, and loading them is the crash that wedged
production in June — and it only removes names the cache itself wrote, so leases, claim files and
temp leftovers are untouchable. The worst case of removing one version too many is a cache miss,
which recompiles.

If your deployment wants a wider window: `AssemblyCache:Retention:KeepVersionsPerType`.

## Boot says which copy of each module pack it loaded

A view-pack fix could merge, build, land in the module store — and the portal would keep serving the
old behaviour, with every lane reporting green. On 2026-08-25 a running portal had memory-mapped the
*image's* copy of a pack while the module store held two newer copies that both contained the fix.
The only evidence lived inside a production pod.

Every step was intentional; nothing said so out loud. A pack listed in the deployment's baseline
resolves to the image copy, and the store's record of the newer copy is then quietly deduplicated
away by name.

Startup now prints one line per pack — where it loaded from, its identity, when it was built — and a
loud **STALE PACK** warning when the module store holds a copy that is both newer and genuinely
different. Two copies of the same bytes in two places say nothing, so the warning stays meaningful.

```
[ModuleLoad] MeshWeaver.Blazor.Views ← /app/modules/… (source=appsettings, mvid=1a2b3c4d, written=…)
[ModuleLoad] STALE PACK: MeshWeaver.Blazor.Views is loading /app/modules/… while the module store
             holds a NEWER, DIFFERENT copy at /data/modules/MeshWeaver.Blazor.Views@12d2c7c2/…
```

It warns and boots. It never refuses to start: a portal that will not come up cannot be given the
module that fixes it.

## A bloated replica raises an alert

On 2026-08-25 two portal replicas climbed from 2.7 GB to 17 and 20 GB over four hours, pegged four
cores collecting garbage, and served hung pages **for three and a half hours** — passing every probe
the whole time, because the liveness check only asks whether the process is up. Nobody knew until
someone looked at memory by hand.

That memory curve now has a watcher: an alert fires when one portal replica's working set runs more
than three times its lightest sibling and above 8 GB for fifteen minutes. It is detection, not a cure
— the cure is instances converging onto each newly published build — but this class of failure, a pod
degrading while it still looks healthy, will have other causes, and they all share that shape.
