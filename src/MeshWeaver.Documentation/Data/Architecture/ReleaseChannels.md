---
Name: Release Channels
Category: Architecture
Description: A channel is a named, moving pointer to an immutable release — `3.0.0-latest` is the newest sealed set, `3.0.0-stable` the newest that earned trust. One vocabulary for CI, for deployments and for every plugin, replacing four private judgements about how proven a release is. The rule that keeps it safe — a channel names what to SELECT, and the selection always resolves to an immutable id that is what gets pinned, recorded and run.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="6" y1="3" x2="6" y2="15"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/></svg>
---

# Release Channels

**A channel is a named, moving pointer to an immutable release.** `3.0.0-latest` is the newest
sealed set; `3.0.0-stable` is the newest one that has earned trust. A deployment declares which
channel it follows, CI declares which channel it builds against, and every plugin's releases carry
the same two names.

## Why — four private answers to one question

Every consumer of a release asks "how much can I trust this?" and, until channels, each answered it
privately and differently:

| Consumer | What it resolved | Where that rule lived |
|---|---|---|
| a `main` CI run | the newest sealed set | `resolve-platform.py` |
| a pull-request CI run | the newest set **this repo's `main`** has passed on | the same file, a different flag |
| a deployment | whatever its overlay pinned | a Helm values file, by hand |
| a plugin install | an exact version | the install record |

Four rules, no shared name, and no way to say "give me the cautious one" in any of them.

**What that cost, measured 2026-09-15.** `main` resolved core CD 8702 while every open pull request
resolved CD 8654 — 33 core merges apart. Five separate reds appeared on `main` at once, and each one
was a test here pinning behaviour a newer core had deliberately changed. Every fix was *false* on the
set its own pull request compiled against, and `main` could not pass a newer set until those fixes
landed. That is a deadlock, and it is not one an admin merge should resolve. The escape hatch built
for it — a `platform:newest` label on a pull request — works, but it is a workaround for a missing
concept. This page is the concept.

## The one rule

> **A channel names what to SELECT. The selection always resolves to an immutable id, and that id is
> what gets pinned, recorded and run.**

Nothing downstream ever runs a moving name. "Deploy `3.0.0-stable`" means *select the newest
immutable set that channel currently admits, and pin that* — never *run the tag `3.0.0-stable`*.

This is not new law. Three places already enforce it, and a design that broke it would be rejected by
code that is already shipping:

- `VersionSelect.MovingPointer` matches `-latest$` and refuses such a tag as an update candidate:
  *"A pointer is not a version: it names different bytes tomorrow, and rolling a Deployment onto it
  makes 'what does this install run' unanswerable. Never a candidate, under any policy or pattern."*
- `ImageLine`: *"A pointer is a SEED, never a running pin — an instance left naming only a moving tag
  is unlockable and its digest is purge-eligible."*
- The CI invariant that a platform image is pinned by **digest**, never by a moving tag.

The reason is the same in all three: an installation must be able to answer *what exactly am I
running*, and a moving name cannot answer it.

## Two families

**Platform channels** — `3.0.0-latest`, `3.0.0-stable` — resolve to a sealed platform **set**
(`3.0.0-ci.NNNN`), which carries its core commit and its tester and portal image digests.

🚨 **A sealed set does NOT imply a `plugins` publication for its identity.** The seal is the trio —
promote, verify, platform bake — and nothing else, so a set can seal while the publication baked
against its framework identity does not exist yet; the resolver finds that publication separately
and may legitimately pair a set with an *older* one. A platform channel therefore resolves to a
**pair** — the set, and the publication resolved under its identity — and must say which of the two
it is asserting. A channel that named only the set would promise something the seal does not.

**Plugin channels** — `<Package>@latest`, `<Package>@stable` — resolve to a published package
**generation**, identified by content, not to a bare version string.

🚨 **A package version is not by itself an immutable identity.** An equal-version republish MOVES
the head: a rebuild of unchanged source against a newer platform republishes under the same version.
So `Store@1.2.3` can name different bytes on two different days, and a channel that resolved only to
a version would break this page's own rule — two runs following one channel could take different
bytes and both be "correct". The channel resolves to the version *and* the content-addressed
generation, and records the generation.

Channel names are scoped to a release line, so a `3.1.0` line gets its own channels and a deployment
on `3.0` is never dragged across a minor by a name it did not change.

## `latest` is derived; only `stable` is promoted

This asymmetry is the whole reason the design is small.

**`latest` needs no new state anywhere.** For CI it is "the newest sealed set", which is exactly what
`main` already resolves. For the image it is the line pointer CD already publishes on every promote
(`3-latest`, `3.0-latest`, `3.0.0-latest`). For a plugin it is the entry the registry already sorts
first — `WithPublishedModules` orders by version descending, and the head is decided by
`ModuleActivation.KeepsHead`, *"the highest version the shelf holds, never the last upload to
arrive"*. Naming `latest` names a fact the system already produces.

**`stable` is a promoted pointer**, and it is the only piece that needs a writer. A broken promoter
therefore degrades to "stable stops advancing" — never to "nothing resolves".

🚨 **`stable` is a channel over the CI line, not a synonym for an official release.** The two are
different questions and must not be conflated: the official release lane has never run, and the
newest release tag is a release candidate. `3.0.0-stable` means *the newest `3.0.0-ci` set that
passed the evidence below*; it makes no claim about `3.0.0` the product version.

### What promotes a set to `stable`

Evidence, not time. A set is promoted when all three hold:

1. **It is sealed** — the `promote`, `verify` and `platform bake` jobs all green, plus its plugins
   seal. Already computed.
2. **Every satellite's `main` has passed on it** — the same evidence the per-repo ceiling reads
   today, from each repo's own `Platform for this run` annotation, generalised from one repo to the
   fleet and computed once centrally rather than by every pull-request run.
3. **The canary is clean** — the canary lane reports today and gates nothing; here it becomes a
   signal.

Promotion is **monotonic**: `stable` never moves backwards. A set is never un-promoted, because a
consumer that already took it cannot un-take it.

🚨 **Monotonic is a property of the WRITER, not a hope about the write.** Two promotion runs can each
observe a qualifying set and update the same pointer out of order, and an ordinary tag update is a
blind overwrite with no comparison against the release ordinal — so the later write can move `stable`
backwards while every individual step looks correct. The promoter must therefore be **serialised**,
and the write itself a **compare-and-swap** that reads the current target and refuses one whose
ordinal is not strictly greater. Stating the invariant without enforcing it is how it gets violated.

### Where the pointer lives

A moving **git tag in core**, `channel/3.0.0-stable`, at the promoted set's core commit. CI resolves
it with one call, and a commit sha is exactly the shape the resolver already knows how to turn into a
set.

🚨 **The tag is the POINTER, and it is not the audit trail.** Force-updating a remote tag replaces
the ref; there is no durable remote reflog, and an annotated message can only ever describe the
target the tag has *now*. So each promotion is additionally written to an **append-only record** —
the set, the evidence that qualified it, and when — which survives the next promotion. The pointer
answers "what is stable"; the record answers "why, and what was stable before".

CD publishes a mirrored image pointer `3.0.0-stable` beside the `3.0.0-latest` it already publishes.

🚨 **These are TWO writes and they are not atomic**, so "one writer, one step" is not a guarantee: a
Git ref update and a registry tag push succeed independently, and a failure or a retry between them
leaves the two naming different releases with nothing to notice it. The contract is therefore
ordered and one-sided:

- **The Git tag is authoritative.** It is what CI resolves and what the promotion record describes.
- **Everything else is published only after the tag update is CONFIRMED** — the image pointer and
  the promotion record both — never before it and never in parallel. So the window can only hold
  followers that lag, never one that leads, and a crash before the tag lands leaves no trace of a
  promotion that did not happen.
- **The record is inside the protocol, not beside it.** Writing it first would let it describe a
  promotion whose tag never landed; leaving it out of the failure path would let `stable` advance
  with no durable evidence. It is written after the tag, and a tag with no record is a repairable
  state the reconciler fills in — the tag is what happened, the record is the account of it.
- **Convergence needs a TRIGGER, not just a comparison.** Reconciling "on every promoter run" is
  only safe while promotions keep coming: a tag that lands and a registry push that then fails, on
  the *last* promotion before a quiet week, leaves the image pointer behind indefinitely. So the
  reconciler runs **on a schedule of its own**, independent of whether anything was promoted, and it
  is the same idempotent operation — re-derive the followers from the authoritative tag. A
  divergence it cannot repair is **alerted**, not retried silently.

Each follower write is idempotent, which is what makes retry and scheduled repair the same code
path rather than two.

## What a channel is NOT

A channel is **policy: which release you are offered**. It is never **proof: whether you may take
it**. Those are separate, and both still apply:

- **The per-instance combo gate.** Before a portal adopts a candidate, that exact combination —
  candidate image × the module versions *that instance* installed — is built and its tests run. See
  [Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol). Following `latest` means
  "offer me new things sooner"; it never means "skip the check".
- **The floor.** A repository declares the oldest set its own source can still compile against. The
  floor is a veto and binds every path — a channel may not name a set below it, exactly as an
  explicit freeze may not.
- **A freeze.** An explicit pin still overrides a channel. A freeze is an instruction; a channel is
  a preference.

A channel that could bypass any of these would be a way to make a red set look green, which is the
opposite of what it is for.

## How each consumer reads a channel

**CI.** The resolver takes a channel name and resolves the pointer to a set. Pull requests follow
`stable`; `main`, the release dispatch and the daily poll follow `latest` — which is what makes
`main` the place a regression shows up first, and keeps pull requests off it until it is proven.

🚨 **A `stable` target that cannot be resolved FAILS CLOSED.** An earlier draft of this page said
resolution would fall back to "newest sealed **at or below** that set" and called it graceful
degradation. It is not: if the set `stable` names has been purged, quietly taking an older one makes
`stable` denote a release that never earned the channel's evidence — which contradicts the channel
contract, and contradicts the retention rule below in the same page. A promoted channel that cannot
find its target is a **refusal**, and it is an incident about retention rather than a resolution
outcome. At-or-below remains what best-effort resolution does; it is not what a promoted channel
does.

This **replaces** the per-repo ceiling: the ceiling's rule becomes the promotion rule for `stable`,
computed once for the fleet instead of re-derived by every pull-request run. The `platform:newest`
label generalises to naming a channel.

**Deployments.** The layer that already selects is the instance's own update policy — its declared
policy, its pattern and its `requireCiGreen` — and a channel is admitted there. That layer is the
channel's direct ancestor: a pattern like `3.0.0-ci*` already says "the 3.0.0 continuous line", and
the fleet's records already carry a policy and a pattern rather than a version.

🚨 **The resolved id is NOT written back onto the deployment record.** The fleet has moved to no
record-level pins at all — a machine check refuses any value, with no allow-list, and the overlay
image is a floor rather than a pin. So the immutable id a channel resolves to is recorded on the
instance's own side, or as that floor; a design that "resolves the pointer and pins it on the
record" would be refused by the gate that already guards this.

Three properties of that layer must survive contact with a channel, because each was bought with an
incident: **absent stays fail-closed** (an undeclared policy is `None`, and a null pattern admits
nothing); a new field must not put its safe value where the serializer will omit it; and where a
cache or a denominator is involved, **it may only fail in the holding direction** — a stale read may
make an instance hold, never roll. A channel must inherit that asymmetry rather than relax it.

**Not every estate follows the fleet line.** A stand-alone client estate keeps its own records in its
own repository, and may deliberately run a slower policy with an explicit pin. A channel is offered
to such an estate, never imposed on it.

**Plugins.** `latest` is a label over a fact the registry already computes: the index orders each
package's entries newest-first, so naming the head costs a field on the projection and no new state.

`stable` is not that, and the difference should not be glossed. The index carries no channel
metadata today and there is **no promotion writer for a package** — so a plugin `stable` needs a
durable field on the registry's own record, a publisher that sets it, and resolution on the consumer
side; it cannot be an otherwise-unchanged filter over the existing ordering. That is why the two
channels are separate pieces of work rather than one, and why `latest` can ship long before `stable`.

What does carry over unchanged is the adoption decision: "take the newest served, never roll back"
*is* channel-following, once the channel decides what is served.

## What a channel needs from the system around it

A pointer is only as good as the things it points at, and two of those are not automatic.

**The set a channel names must be RETAINED for as long as anything may still resolve it.** This is
the invariant most easily missed, and it has already been measured the hard way: when the CI platform
volume kept only the three newest sets while core cut one every 20–45 minutes, its window became
shorter than the CI queue. A pull request resolved a set, waited an hour behind a saturated runner
pool, and then failed because that set was **below the oldest set kept** — thirteen of twenty-seven
open pull requests at once, on unrelated diffs. A moving pointer plus a short retention window is a
race, and an id being immutable does not save you if the artifact behind it is gone. So promotion
must also mean **retention**: the set `stable` names is kept while it is named, and for as long
afterwards as any consumer may still be holding it.

Note that retention is by digest, and record-level pins used to supply some of its anchors. A fleet
that drops its pins and a lane that stops locking moving tags are each correct on their own; together
they must not add up to "nothing anchors what we still need".

**An instance too far behind cannot re-enter a channel on its own.** The availability gate an
instance runs is the gate in the image it is *currently* running, so an installation several months
old can judge a newer publication unreadable and hold — while a newer instance, reading the same
registry, rolls the same set happily. Following a channel does not repair that; it takes one
deliberate roll to bring such an instance forward, after which the channel governs again. A channel
design should say this rather than let it surface as "the pointer resolves correctly and the instance
ignores it".

**A gate that never answers is not evidence.** The per-instance combo verification is the thing that
would justify the word "trust", and where it has not been provisioned it returns *unverified* rather
than a verdict. `stable`'s promotion rule must lean only on evidence that actually answers, and say
which evidence it used.

## Naming

`<line>-<channel>` for the platform (`3.0.0-stable`), `<Package>@<channel>` for a plugin
(`Store@latest`). Lower case, and the channel word is one of a closed set — a channel is a promise
about *how proven*, not a free-text label, and an unrecognised one must be refused rather than
silently treated as a pin.

## See also

- [Candidate Release Protocol](/Doc/Architecture/CandidateReleaseProtocol) — the states a version
  moves through, and why the deploy boundary is where the framework's gate belongs.
- [Module Adoption Policy](/Doc/Architecture/ModuleAdoptionPolicy) — what an installation runs, and
  why loadability is measured rather than declared.
- [Module Versioning](/Doc/Architecture/ModuleVersioning) — where a plugin's version comes from, and
  why a published version describes exactly one tree forever.
