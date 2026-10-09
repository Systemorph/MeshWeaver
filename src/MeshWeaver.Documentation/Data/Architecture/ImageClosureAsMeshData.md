---
Name: ImageClosureAsMeshData
Category: Architecture
Description: An image's closure (what is in /app, per platform, per file) as an ImageClosure node the CD build writes for every image it publishes — keyed by digest, read through a coverage statement in which an absent record is NOT MEASURED
Icon: Database
---

# An image's closure, as mesh data

**Policy `image-closure-as-mesh-data`** ([register](../PolicyNotProse)): an image's closure becomes
queryable mesh data — a node type written by the CD build per image — and every gate that reads it
states its coverage from it. Tracked on
[#4066](https://github.com/Systemorph/MeshWeaver/issues/4066).

## Why

"What is in `/app`" had exactly one answer: `.github/scripts/check-platform-reference-set.sh`, a
producer-side assertion that leaves no record. Every later reader re-derived it — #3328 was
diagnosed with `docker run … ls /app`, and retention
([Fleet Registry Retention](../FleetRegistryRetention) §5) still has no inventory to compute a
deletable set against. The assertion stays exactly where it is; what changes is that the bytes it
accepted are now **written down**.

## The shape

| part | where | what it does |
|---|---|---|
| the record builder | `.github/scripts/image-closure-record.py` (`--self-test` in CI) | runs over the SAME publish directories `check-platform-reference-set.sh` just accepted, **after** it passed, and emits one `image-closure` record: repository, digest, the tags known at write time, platform commit, platform version (opaque — no `-ci` notation assumed), framework identity, and per platform its `fileCount`, the surface manifest's hash and every file's path + SHA-256 + size |
| the producer step | `main-cd.yml` `portal-image` → *Record the asserted closure* | reads the pushed image's digest, builds the record, uploads it as the `image-closure` artifact |
| the delivery | `main-cd.yml` `image-closure-record` | signs the record with the control plane's existing HMAC (`secrets.CONTROL_WEBHOOK_SECRET`) and POSTs it to `/api/hooks/Admin/ImageClosures` on the control plane's host; red unless the answer is **accepted + verified** |
| the ingest | `ImageClosureIngest` (`src/MeshWeaver.Graph/ImageClosures`) | drains `Admin/ImageClosures/_Inbox`, re-verifies the HMAC over the stored body, validates the record (`ImageClosureRecord.TryParse`), writes `Admin/ImageClosures/{repository}-{64 hex}` as System, deletes the delivery |
| the reader | `ImageClosureCoverage` | the coverage statement every gate prints (below) |

**Identity is the digest.** A retag or a promotion never changes it, so a record is written once per
built image and never moved. `Tags` is an attribute — the tags the writer knew — never the complete
set the image ever carries, and a re-delivery replaces the record wholesale (no fold, so no
create-if-missing race).

**Outside the delivery verdict.** Nothing `promote` or `delivery-verdict` needs waits on
`image-closure-record`: a control-side outage can never stop an image from shipping. It is still red,
by name, whenever the record was not accepted verified — never green-by-skip.

**Armed only where it can verify.** The ingest is registered on every portal
(`MemexConfiguration` → `AddImageClosures()`) and arms only where `WebhookInbox:Targets` lists
`Admin/ImageClosures` **with** a `SecretConfigKey`. A target allowlisted without one is refused at
Error: an unsigned record would let anyone who reaches the endpoint write the data a gate trusts.

## Reading it: coverage, never a bare answer

A mesh read can answer *smaller* rather than wrong — a record never written, written to another
instance, or outside the reader's reach all look like "no row". So
`ImageClosureCoverage.Read(hub, expected)` takes the images the caller **expects** and reports:

* `N of M expected image(s) measured from Admin/ImageClosures`,
* every expected image with no record, by `repository@digest`, as **NOT MEASURED**,
* `AllMeasured` true only when `M > 0` and every one was read — zero asked is not a verdict.

A gate that depends on the data prints the `Statement` with its verdict and treats NOT MEASURED as
red. The listing is one `scope:children` query (a set read, the valid use of a query): an index that
trails the store can only report an image as NOT MEASURED — the loud direction — never invent one.

## What it does not do

* It does not replace `check-platform-reference-set.sh`. The assertion is still the producer-side
  gate, before `promote`; the record is what it asserted.
* It is not the registry inventory [Fleet Registry Retention](../FleetRegistryRetention) §5.1 needs:
  that is *what the registry holds*, including what other producers pushed. This records *what this
  CD built*, which is one input to it.
* Only `memex-portal-ai` is recorded today — the image `check-platform-reference-set.sh` asserts.
  The other images in the set have no closure assertion to record.

## Turning it on — the control-record half

The control plane must allowlist the target before the first record lands; until then
`image-closure-record` is red with *"does not allowlist the inbox target Admin/ImageClosures (404)"*.
On the control plane's `Deployments/<id>` record, add a `webhookInbox` slot — target
`Admin/ImageClosures`, `secretConfigKey` `Hosting:PlatformWebhookSecret` (rendered as
`WebhookInbox__Targets__N` / `__SecretConfigKey`) — then `Reconcile`. No new secret: it is the same
HMAC the control lane already uses. Verify with `get @Admin/ImageClosures/*` on the control instance
after the next CD run.

## Related

* [Fleet Registry Retention](../FleetRegistryRetention) — §5, the inventory this feeds
* [Module Build Architecture](../ModuleBuildArchitecture) — the platform image is the reference set
* [Search Coverage and Refusal](../SearchCoverageAndRefusal) — why a reader states its denominator
