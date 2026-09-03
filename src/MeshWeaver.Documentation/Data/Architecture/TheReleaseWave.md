---
Name: The Release Wave — one emitter, and who resolves the digest
Category: Documentation
Description: How a publication becomes a repository_dispatch across the plugin fleet — the single emitter rule, the image/digest contract that puts resolution on the RECEIVER, and the transitional gap left by retiring the GitHub-to-GitHub job
---

A module is built against a platform **pin**, so when the platform (or an upstream catalog)
publishes, every dependent repo must rebuild or its portals read `FrameworkDeclined` and adopt
nothing. The mechanism that wakes them is the **release wave**: one `repository_dispatch` per
subscribed repository.

This page exists because the wave has been mis-diagnosed twice in one day, in opposite directions —
once by blaming a receiver's pin, once by blaming an emitter that did not exist. Both mistakes are
cheap to repeat, because **the evidence for "who sent this" is not in the repo that received it.**

## 🚨 The wave has exactly ONE emitter — and which one it is depends on a PIN

> Maintainer, 2026-09-03: *"None of the top-level repos should have any dependency to anyone else.
> It must be event based: (1) memex issues an event that something has a new version; (2) GitHub
> subscribes to this and triggers the build. Core publishes an event and finishes."*

That directive **retired** the GitHub→GitHub emitter — the `dispatch-dependents` job in this repo's
`node-repo-publish-bake.yml`. On `main` the input survives only as a `RETIRED 2026-09-03` stub that
is *ignored*, so a caller pinned to an older lane still validates while it moves its pin.

**And that is the trap.** A reusable workflow is consumed at a `@<sha>` pin, so retiring a job on
`main` retires it for *nobody* until each caller moves. A repo pinned before the retirement still
runs the old job, which still dispatches — a **zombie emitter**, live in a caller's `ci.yml` rather
than anywhere you would grep for it.

**So: never answer "where did this dispatch come from?" from `main`.** Read the emitting repo's
pin, then read `node-repo-publish-bake.yml` *at that pin*:

```bash
grep -n 'node-repo-publish-bake.yml@' <repo>/.github/workflows/ci.yml
git -C ~/code/MeshWeaver cat-file -p "<pin>:.github/workflows/node-repo-publish-bake.yml" \
  | grep -n 'dispatch-dependents:'
```

A corollary worth stating because it has already cost a wrong issue: **an in-mesh node is not the
emitter merely because no committed source explains the dispatch.** In-mesh `.cs` compiles at
runtime and is invisible to `dotnet build` and to a `.cs` grep, which makes it a tempting suspect.
Before accusing it, `get` the live node and diff it against `origin/main`. On 2026-09-03
`Hosting/Deployment/Source/PlatformBuildInboxWatcher` (v7) matched the repo exactly and contained
no wave emitter at all — the dispatches were the zombie job above.

## The image/digest contract — the RECEIVER resolves

The dispatch payload carries both forms, and they are not redundant:

| field | what it is |
|---|---|
| `image` | the full tester-image reference the bake used — **authoritative** |
| `platform_image` | the portal image this bake compiled against (MeshWeaver#3022) |
| `digest` | a **convenience**: the part after `@`, and **empty when `image` was a tag** |

```sh
# A receiver's own gate wants the bare digest (its image-digest input); the bake wants
# the full reference. Carry both — the digest is the part after '@' when the image was
# resolved by digest, empty when it was a tag (then the receiver resolves it itself).
digest=""; case "$IMAGE" in *@sha256:*) digest="${IMAGE#*@}" ;; esac
```

🚨 **An empty `digest` beside a present `image` is the contract WORKING, not a contract break.**
The parenthesis — *"then the receiver resolves it itself"* — is the receiver's obligation, and a
receiver that refuses instead throws away well-formed waves. MeshWeaver.Manufacturing did exactly
that for ten consecutive red `main` runs on 2026-09-03 before its preflight was taught to resolve
`client_payload.image` (Manufacturing#50).

**Resolving is not the same as falling back to the pin, and the difference is the whole point.**
The pin *caps* what the gates can see, so gating against it reports a green release-follow for a
framework nobody checked. Resolve the wave's own image and fail LOUD when you cannot:

```sh
digest=$(docker manifest inspect -v "$WAVE_IMAGE" \
  | jq -r 'if type == "array" then .[0] else . end | .Descriptor.digest // ""')
```

Not `imagetools inspect --format '{{.Manifest.Digest}}'` — that template reads a member an OCI
manifest does not carry, so it yields nothing and every run takes the fail-loud branch.

## The transitional gap (open as of 2026-09-03)

The directive's end state is: the lane ends with **one signed POST** of the publication record to
memex (`webhook-url` / `webhook-secret`), and **memex** emits `meshweaver-upstream-published`.

The receiving half exists — memex's generic webhook inbox, and
`Hosting/Deployment/Source/PlatformBuildInboxWatcher` draining it. The **emitting half does not**:
`ParseBuild` accepts only `event: "platform-build"` and logs *"ignoring non-build event"* for
anything else, then deletes it. `FrameworkReleaseBroadcaster.Broadcast` already takes the
`eventType` and `payload` arguments a `bundle-publication` branch would need; nothing calls them.

**Therefore the pins must move in this order**, and moving them early is worse than leaving them:

1. Write the `bundle-publication` branch (parse the record, broadcast
   `meshweaver-upstream-published` carrying `source`, `image`, `platform_image`, `digest`,
   `identity`, `version`, `sha`), with a test pinning the **payload**, not just the log line.
2. Provision `webhook-url` / `webhook-secret` on each publishing caller.
3. Then move each caller's pin past the retirement.

Move a pin before step 1 and the publication POSTs to an inbox that discards it: the zombie
emitter stops, nothing replaces it, and **the wave dies silently** — every dependent falls back to
its daily schedule poll, which is exactly the "absence of evidence read as evidence" failure the
whole lane is built to refuse.

## Reading a wave, in order

1. **Who emitted it?** The receiver's run says `repository_dispatch` and an actor; it does not say
   which repo. Find the caller whose `bake-source` matches `client_payload.source`, then read its
   pin (above) — not core `main`.
2. **Who subscribes?** The old job filtered on the dependent's own `ci.yml` declaring the source
   under `upstream-sources:` / `upstream-seed:` — a line-anchored match, so a mention in a comment
   does not subscribe a repo. The end-state answer is data in the mesh: a `Hosting/Deployment`
   record's `pluginRepos[].isRegistrySource`. **Never a list in configuration** — every earlier
   design kept a second copy of that graph and each copy was empty on the deploy that mattered
   (MeshWeaver#2235, Memex#140).
3. **What did it name?** `image` first, `digest` only as a shortcut, `platform_image` for the
   portal to pair with. A wave that names no image at all is the only genuine contract break.
