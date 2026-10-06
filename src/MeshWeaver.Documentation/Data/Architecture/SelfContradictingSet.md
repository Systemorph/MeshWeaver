---
Name: A Set That Contradicts Itself
Category: Architecture
Description: How two CD runs of one core and Plugins pair produced platform set 3.0.0-ci.9985 with a 9985 tester and floor next to a 9984 portal, and the three gates that stop such a set from being armed, sealed or pointed at.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h7"/><path d="M13 17h7"/><path d="M7 4v6"/><path d="M17 14v6"/><path d="M5 19L19 5"/></svg>
---

# A Set That Contradicts Itself

A platform **set** (`3.0.0-ci.<n>`) is one tester, one portal and one migration image, all built by
the same `main-cd` run. Consumers also take it as one thing. The satellites' bake gate pairs the
set's tester with the set's portal. A bundle carries a platform **floor**, which is the version of the
process that produced it, and a consumer running an older build **declines** that bundle
(`PlatformCompatibility.DeclineReason`). When the halves of a set come from two different runs, every
consumer of the set fails, and the failure looks like a satellite problem.

## What happened (MeshWeaver#6180, MeshWeaver.Manufacturing#84)

Run 9984 (an hourly reconcile tick) and run 9985 (a `rebuild` dispatch) both built core `d205295`
together with Plugins `29bfaef`. The two runs sit in different concurrency lanes, so they overlapped,
and both built a complete set. Run 9984 promoted three minutes **after** run 9985. Promote writes
tags that name the **pair**, not the run: `memex-portal-ai:<core7>-p<plugins7>`, the bare `<core7>`,
and `main`/`latest`. Those tags therefore ended up on run 9984's images.

- A consumer resolving set 9985 took the tester from `mw-plugin-test:3.0.0-ci.9985`, a version tag
  that only run 9985 writes. It took the portal from the pair tag, which run 9984 had re-pointed at
  an image reporting `3.0.0-ci.9984`.
- Run 9989's `arm` then copied that pair tag onto the **arming** tag, so
  `memex-portal-ai:3.0.0-ci.9985` names an image that reports 9984.
- The Plugins publication of set 9985 was baked consistently, with floor 9985, against run 9985's own
  portal. Manufacturing's gate ran in the 9984 portal and declined 134 of 149 Plugins types:
  *"its platform floor 3.0.0-ci.9985 … is NEWER than the running platform 3.0.0-ci.9984"*.

It recurred: the pair tags of sets 9989 and 9993 name the portals of runs 9988 and 9991.

## The root fix: identity by run

A tag that two runs can write cannot identify a set. The per-run staging tag
`staging-<core7>-<run id>` can, and so can the digest of that tag. `arm` and the resolver therefore
read the set's portal from the staging tag (MeshWeaver#6177), never from a tag that names a pair or
a commit.

## The gates: a contradicting set is never armed, sealed or pointed at

Each gate below runs unconditionally on the path it protects. Each one fails closed: if it could not
read something, it is red.

| Where | Gate | It refuses |
|---|---|---|
| `main-cd.yml`, `arm` | `check-image-build-identity.sh <source> <version>` before phase C | writing `memex-portal-ai:<version>` onto an image whose `MESHWEAVER_PLATFORM_VERSION` (on any architecture) is not `<version>`. This is the write that produced the 9985 incident. |
| `node-repo-publish-bake.yml`, after the bake | `assert-bake-floor.py --bake … --platform-version …` | publishing any bundle whose `producerPlatformVersion` is above the version reported by the portal the publication record names. Zero bundles, an unreadable bundle, a missing floor, or a floor that cannot be ordered against that version are all red. |
| `main-cd.yml`, `promote` phase B | never backwards | moving `main`/`latest` when a newer set (a higher `mw-plugin-test:<line>-ci.<n>`) has already been promoted. This set's own version tags are still written. |

Floors are ordered as `PlatformReleaseOrder` orders them. Two continuous builds compare by run
number, and two clean releases compare by `X.Y.Z`. A continuous build cannot be ordered against a
clean release, and the floor gate refuses that pair, because a bake runs **inside** the portal it
names: a mixed pair means the lane composed two different builds.

## Reading the symptom

A satellite red that reads *"floor … NEWER than the running platform …"* across **every** upstream
type does not mean the satellite is broken. It means the set it resolved has two halves. Check this
first:

```bash
crane config meshweaver.azurecr.io/memex-portal-ai@<portal digest the run pulled> | jq -r '.config.Env[]' | grep MESHWEAVER_PLATFORM_VERSION
```

Then compare the result with the tester's version tag. If the two disagree, the cause is in core CD,
and re-running the satellite will not help.

## Related

- [Image Tag Contract](../ImageTagContract) — which tag means what.
- [One Promotion Gate](../OnePromotionGate) — what `arm` writes and when.
- [CI Content Bake](../CiContentBake) — the publish-bake lane the floor gate guards.
- [Bake Identity Mismatch](../BakeIdentityMismatch) — the sibling failure, a mismatch of addresses rather than versions.
