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
(`3.0.0-ci.NNNN`), which already carries its core commit, its tester and portal image digests, and
its sealed plugins publication.

**Plugin channels** — `<Package>@latest`, `<Package>@stable` — resolve to an exact published package
version.

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

### Where the pointer lives

A moving **git tag in core**, `channel/3.0.0-stable`, at the promoted set's core commit. Its
annotated message records the evidence, so the tag's own history is the audit trail. CI resolves it
with one call, and a commit sha is exactly the shape the resolver already knows how to turn into a
set.

CD publishes the mirrored image pointer `3.0.0-stable` in the same step that moves the tag, beside
the `3.0.0-latest` it already publishes — one writer, one step, so the two cannot drift apart.

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

**CI.** The resolver takes a channel name, resolves the pointer to a set, and then behaves as it
always did — including "newest sealed **at or below** that set", which degrades gracefully when the
pointed-at set's images have been purged, where a hard freeze would go red. Pull requests follow
`stable`; `main`, the release dispatch and the daily poll follow `latest` — which is what makes
`main` the place a regression shows up first, and keeps pull requests off it until it is proven.

This **replaces** the per-repo ceiling: the ceiling's rule becomes the promotion rule for `stable`,
computed once for the fleet instead of re-derived by every pull-request run. The `platform:newest`
label generalises to naming a channel.

**Deployments.** Platform config declares a channel instead of a pinned tag. The layer that already
selects — the instance's own update policy, with its pattern and its `requireCiGreen` — admits the
channel by resolving the pointer to a digest and pinning the immutable tag that carries it.

Two properties of that layer must survive contact with a channel field, because both were bought
with incidents: **absent stays fail-closed** (an undeclared policy is `None`, and a null pattern
admits nothing), and a new field must not put its safe value where the serializer will omit it.

**Plugins.** The registry labels the head of each package `latest` in the index it already computes;
a promoted release additionally carries `stable`. A consumer that follows a channel filters the index
on the name and is otherwise unchanged — the adoption decision already means "take the newest
served, never roll back", which *is* channel-following once the channel decides what is served.

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
