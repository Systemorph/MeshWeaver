---
Name: The Build Server
Category: Architecture
Description: Why CI builds only the image, how the bake becomes git-sync-plus-compile on a dedicated worker, and how a dependency graph decides which modules a release forces to recompile
Icon: Wrench
---

# The Build Server

**memex is the coordinator.** It holds the dependency map, and every repository subscribes to it.
Nothing polls, nothing is scheduled, and no repository needs to know what depends on it.

## The protocol

```
1.  CORE      builds, runs its BASIC tests, produces the docker image
              →  notifies memex: a new image exists

2.  PLUGINS   subscribed to memex, are notified, and build against that image
              →  notify memex: these packages, at these versions

3.  MEMEX     knows the dependency map, and notifies repositories to rebuild —
              either FULLY, or only the dedicated packages that are affected
```

Each step ends by telling memex what now exists. Each next step begins because memex said so. That
is the whole cascade, and everything below is a consequence of it.

🚨 **Core runs its OWN tests and nothing else.** It does not compile plugins, does not test them and
does not bake them. A core build that also builds the plugin repositories couples the two: it makes
core's green depend on plugin content, spends core's runners on plugin work, and — as happened on
2026-08-26 — lets one repository's CI starve the other's out of a shared runner pool. Core's output
is an **image plus a notification**; step 2 is where plugins are built, in the repository that owns
them.

CI's job is therefore to **build the plain image and finish**. Everything else is built *from* that
image, on a worker that is not a CI runner.

```
CI            build image → push → notify memex        fast, few runners
build worker  git sync → C# compile → bundles → disk   CPU-heavy, off the shared pool
environments  seed bundles matching their own identity
```

This page is the design. It exists because the previous arrangement failed in production on
2026-08-26 in a way that was invisible until a user noticed: CI starvation meant no image could
publish, plugin modules kept republishing against a newer core, and the portal — pinned to the older
image — crashlooped on a module's hosted service while serving eleven-hour-old code.

## 1. The bake never stands up a mesh

**A bake is git sync, then C# compile.** Nothing else.

The rule is settled ([Ci Content Bake](/Doc/Architecture/CiContentBake), #2064):

> Producing an assembly is a **build** step; the mesh's job is to **consume** a bake, not to be the
> thing that makes one.

The abandoned shape was one fused run — `mw-plugin-test <stage> --bake-output <dir>` — that stood up
an in-process mesh and let the *mesh* compile every NodeType. It is wrong twice over. It activates
hubs to manufacture bytes nothing has adopted yet, which is where it crashes; and as a gate it is
weaker, because the mesh then renders and tests a **private recompile** — bytes nothing ever ships.

The split is two commands, and `bake-then-gate.sh` holds them together so lanes cannot drift:

```bash
mw-plugin-test compile <stage> --output <bake> --source-sha <sha>   # 1. compiler. no mesh.
mw-plugin-test <stage> --seed <bake>                                # 2. a mesh CONSUMES the bake
```

Step 2 exercises the very assemblies the lane is about to publish — the ones a portal adopts.

🚨 **Only step 1 belongs on the build worker.** It needs no mesh, no database, no environment. That
is precisely what makes it liftable out of CI.

## 2. The worker is a node pool that scales to zero

The bake runs in the cluster, not on a VM, because the **write path collapses**:

| | CI today | build worker |
|---|---|---|
| publishing a bundle | `az storage file upload` to every target account | a **local write** to a mounted PVC |
| credentials needed | `Storage File Data Privileged Contributor` per account | none — it mounts the share the portal mounts |
| configuration | `BAKE_PUBLISH_TARGETS`, preflighted | the namespace it runs in |

```bash
az aks nodepool add -g <rg> --cluster-name <cluster> \
  --name bake --node-vm-size Standard_D48ads_v5 \
  --enable-cluster-autoscaler --min-count 0 --max-count 2 \
  --node-taints workload=bake:NoSchedule --mode User
```

- **`min-count 0`** — the node does not exist between bakes. Roughly $160/month at two hours a day,
  against ~$1,900 for an always-on machine of the same size.
- **The taint is load-bearing.** The `silos` pool runs the portal and the Orleans silos; a bake
  saturating it would compete with production. Bake pods carry the matching toleration and land
  nowhere else.
- The migration `Job` in the chart is the shape to copy — `restartPolicy: Never`,
  `ttlSecondsAfterFinished`, one per environment namespace.

**Input contract:** the image, the git to sync, and which packages to run.

### The checkouts are persistent

The worker keeps each repository checked out and **updates to the target commit hash**, rather than
cloning per bake. A bake is dominated by compilation, not by fetching, and a warm working tree plus a
warm NuGet cache is most of the difference between a bake that fits in a coffee break and one that
does not. It is also why a dedicated worker beats an ephemeral runner for this workload: a CI
container throws that state away every time.

## 3. Architecture is part of the identity

A bake is an ABI claim about **bytes**, so it is valid only for the architecture it was taken on —
four reference assemblies genuinely differ between the amd64 and arm64 variants of one image, and
each resolves its own framework identity.

- The **amd64** lane is production-critical (every AKS node is amd64) and belongs in the cluster.
- The **arm64** lane serves Apple Silicon developer machines and `memex-local` sidecars. If it is
  late, those installs compile at boot exactly as they do today — **degraded, never broken** — so it
  may live somewhere less redundant.

🚨 Publishing one architecture's bytes under the other's identity is the *adopt-what-you-did-not-
resolve* defect. `publish-bake-bundles.sh` records the producing architecture and **refuses** a
mismatch rather than skipping or overwriting it.

## 4. Every repo, one pass — and their dependencies

The worker bakes **all repositories** against the new image. The layout already supports several
producers: bundles land at `prebuilt-bundles/<framework-identity>/<source-name>/`, where
`<source-name>` is the producing repository, each with its own `source-commit.txt` and `_complete`
sentinel.

Two properties make "bake everything" cheap rather than wasteful:

- the sealed-skip keys on **content × framework**, so a repository whose content has not changed is
  skipped, not rebuilt;
- **dependencies are simply present.** Today a repo's lane stages other repos' modules *"only so the
  requires resolve"* and must then carefully not publish them. With every checkout on one worker,
  that dance disappears.

This also closes a documented hole. Which repositories must rebuild when an upstream publishes was
answered by each repo's own `schedule` — *"there is no dispatch and no dependent list. A repo missing
the schedule never rebuilds for a release, and the only symptom is an instance HELD on bundles from
that repo's source."*

## 5. The dependency graph decides what recompiles

**Input:** the modules a release publishes, with their versions.
**Question:** across every repository, which modules *depend* on those and must therefore recompile?

```
platform release      ⇒ the closure is EVERYTHING
one module changes    ⇒ the closure is that module's dependent SUBTREE
```

So the rebuild set is the **reverse transitive closure** of the changed modules over the dependency
graph. Rebuilding less is a stale bundle; rebuilding everything on every module change is the cost
this is meant to avoid.

The cascade then runs in dependency order:

```
platform → plugins → reinsurance → education → …
```

**memex decides the scope of each notification**, because it is the only participant that can: a
repository knows what it depends ON, but not what depends on IT. That is why step 3 is memex telling
a repository to rebuild — fully or in part — rather than a repository working it out for itself.

### Everyone subscribes to the mesh, and memex owns the map

Steps 2 and 3 of the protocol are subscriptions, not deliveries. The cascade is **not** a fan of
webhooks. Every participant subscribes to MeshWeaver and reads
**persisted streams**: a release is durable mesh state, and a dependent *queries that state* rather
than catching an event. [The Release Event Bus](/Doc/Architecture/ReleaseEventBus) is the mechanism —
*the bus is the mesh; the webhook is only a wake-up*.

The difference matters at exactly the moment it is hardest to debug. A dispatch is delivered once: a
receiver that is down, throttled or mid-deploy misses it, and the only symptom is a repository that
quietly never rebuilds — the same silence a missing `schedule` produced. Persisted state has no such
edge. A participant that was away reads where it left off and catches up.

### 🚨 A dependent is notified only when ALL of its dependencies are built

A node in the graph becomes eligible when **every** in-edge has completed — never when the first one
does.

```
        A                A publishes.
       / \               B and C rebuild.
      B   C              D waits for BOTH. It is not notified when B alone finishes.
       \ /
        D
```

Building `D` against a half-updated set is precisely the failure that took production down on
2026-08-26: a module was rebuilt against a newer core while the image it had to load into was still
the older one, and every replacement pod aborted at boot. The readiness barrier is what makes
"image and modules move together" a property of the system rather than a matter of timing.

The barrier is also why the graph must be **data**. "All dependencies built" is a question about
edges; it can only be answered by something that knows all of them.

### 🚨 A part is retried at least five times before it is called failed

Infrastructure fails transiently — a runner is reclaimed, a registry rate-limits, a spot node is
evicted mid-compile. In a cascade those are expensive out of all proportion: one transient failure
deep in the graph stalls every dependent behind it, and the barrier above means they wait rather than
proceed.

So a part is retried **at least five times** before the cascade records it as failed.

### 🚨 And when the retries are exhausted, it ERRORS — loudly

Five failed attempts end in a **clear, named error**, never a quiet give-up and never a green tick.
The cascade must say which part failed, on which repository and commit, with the cause from the last
attempt — and the dependents behind it stay **unbuilt**, because a barrier that lets a node through
on a failed dependency is not a barrier.

This is the same rule the delivery lane already learned the hard way: on 2026-08-26 every CD run
reported **success** while publishing nothing, because its gate had been skipped rather than
evaluated. A run that checked nothing must never be indistinguishable from one that delivered. The
build cascade inherits that requirement exactly — *"a gate that cannot fail is not a gate"*.

So: retry the transient five times, then **error, name it, and stop the subtree.**

This does not hide a real defect and must never be tuned so that it could. A deterministic failure —
a compile error, a missing symbol — fails identically five times and is then reported with its own
cause; retrying only costs time. What it removes is the case where a green tree is declared broken
because a runner disappeared. The distinction to preserve: **retry the BUILD, never the verdict.**
Re-running a failing *test* to see whether it passes this time is the opposite habit, and it hides
real races (see [Writing Tests](/Doc/Architecture/WritingTests)).

🚨 **The graph is DATA, not workflow YAML.** A package declares what it requires; that declaration
orders the build. Encoding the same edges in CI configuration means the graph that ships and the
graph that builds can disagree, and only production finds out.

### It belongs in the Store

Once the graph is data it should be visible on a package's **catalog page**: what this package
requires, and what requires it. The same edges answer both questions people actually ask — *what
does installing this pull in?* and *what breaks if this changes?*

## Why CI gets smaller

The bake is the heavy part of CI, and it is the part that does not need a runner. Moving it off the
shared pool matters beyond speed: on 2026-08-26 twelve concurrent runs in a sibling repository
saturated the account-wide 60-job limit, main's runs could not start, no image published, and the
gap between image and modules widened until the portal could not start a pod.

CI keeps what only CI can do — build the image, run the tests, gate the merge — and the bake moves to
a machine that can hold a warm checkout and 48 cores.

See also: [Ci Content Bake](/Doc/Architecture/CiContentBake) ·
[Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) ·
[Deployment AKS](/Doc/Architecture/DeploymentAKS)
