---
NodeType: Markdown
Name: Plugin publication provenance
Abstract: The shared publisher announces the exact plugin content commit and the platform version it built against, independently of the calling workflow's repository and event.
Icon: Package
Tags:
  - Architecture
  - CI/CD
  - Plugins
---

# Plugin publication provenance

A successful plugin publication calls the existing signed Memex inbox at
`/api/hooks/Hosting/PlatformBuilds`. The record must identify the **content that was built**.
Core CD can invoke the same publisher for `Systemorph/MeshWeaver.Plugins`, so the calling
workflow's commit is not necessarily a commit in the repository named by the record.

`node-repo-publish-bake.yml` now passes the resolved content checkout SHA from its existing
`content` step through the job output into the notification. This is the same SHA passed to
the bake and publication scripts. It does not substitute `github.sha`, a branch name, or the
workflow's own checkout when the output is absent.

The record's `version` describes the **build platform**, not a package's SemVer. It is read
from `MESHWEAVER_PLATFORM_VERSION` in the selected, already-pulled portal image configuration,
the same source a running portal uses. Pushes and core CD calls have this fact even when
`github.event.client_payload.version` is absent. A package's own `version` and `moduleVersion`
remain its `manifest.lock` fields at the announced content commit.

| Record field | Source |
|---|---|
| `repo` | Explicit `content-repository`, otherwise the caller's repository |
| `sha` | `git rev-parse HEAD` after the content checkout, also supplied to the bake |
| `version` | The selected portal image's own `MESHWEAVER_PLATFORM_VERSION` |
| `identity` | Existing portal/tester framework identity agreement |
| `image`, `digest`, `platformImage` | Existing resolved tester and portal references; `digest` is the tester image digest, **not a plugin content digest** |
| `run` | The workflow repository and run URL, retained separately from content provenance |
| `upstreams` | The caller's existing normalized dependency declaration |

The version read refuses a missing, duplicated or malformed image configuration value.
"Malformed" means any shape the pipeline does not mint: accepted are exactly clean `X.Y.Z`,
`X.Y.Z-ci.N`, `X.Y.Z-edge.N` and the retired rc line's `X.Y.Z-<label>.ci.N` / `.edge.N` — the
set `PlatformReleaseOrder.BuildOrdinal` reads. A label no build mints (`3.0.0-alpha`,
`3.0.0-preview.1`, a bare `3.0.0-rc9`) carries no build ordinal, so a receiver would order it as
a promotion; both the producer step and the POST refuse it with the same pattern. The
final POST independently refuses an absent/malformed 40-digit hexadecimal content SHA or
platform version. It retains the existing URL, HMAC secret, signed payload, HTTP failure
handling and signature-verdict check. It introduces no credential, alternate callback, direct
repository dispatch or new update permission.

## Fleet audit, 11 September 2026

The organization's repository inventory, six current `ci.yml` callers and GitHub code search
(`org:Systemorph node-repo-publish-bake filename:ci.yml`, six results, not incomplete) identify
these active plugin publishers. All six already supply `PLATFORM_WEBHOOK_URL` and
`PLATFORM_WEBHOOK_SECRET` to the shared lane; all six repository variables point at
`https://memex.meshweaver.cloud/api/hooks/Hosting/PlatformBuilds`, and all six expose the secret's
metadata. The secret values were not read.

| Repository | Audited main | Source / upstream sources | Shared lane ref |
|---|---|---|---|
| MeshWeaver.Plugins | `05fde51007d97e5859682551b94027d190d3491b` | `plugins` / none | `main` |
| MeshWeaver.Education | `67274a2f650f9dcad25a57fa8ad50fceda8634c0` | `education` / `plugins` | `67cbbe0ee4467ac30f086587c97f62c7b0d13689` |
| MeshWeaver.Reinsurance | `17b161a9c2fe6ec50a9764c66bd2b332d7f27621` | `reinsurance` / `plugins crm` | `main` |
| MeshWeaver.SocialMedia | `3fa2cab318bf7442a17b83060e80031e09f2f961` | `socialmedia` / `plugins` | `main` |
| MeshWeaver.Manufacturing | `73e3da77131a1401af20a3545c2acf16ec0b9add` | `manufacturing` / `plugins` | `main` |
| MeshWeaver.Crm | `a55fec3abce3bc448afb45d48d83d856a334f749` | `crm` / `plugins` | `main` |

The publication jobs depend on each caller's validation set and run for default-branch pushes,
scheduled runs and repository dispatches. The shared notification remains downstream of a
successful bake publication with `published=true`; this change does not redefine a skipped or
already-sealed bake as a new publication. Education's pinned caller must adopt a revision
containing this correction through its normal reviewed workflow update.

The three readable `Hosting/Deployment` registrations are a separate inventory. The registry's
four GitHub source declarations name Plugins, Education (through the old `education` alias),
Reinsurance and Crm. SocialMedia and Manufacturing still belong in the publisher fleet despite
not appearing in those declarations. `Memex` is the deployment repository; `agentic-pensions`
has a `Space` root rather than a plugin catalog; `MeshWeaver.Feedback` is an empty triage
repository. They are not additional callers of this publication lane.

This audit establishes sender wiring, not successful delivery or adoption on every instance.
[Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild) describes the
receiver/update path. In particular, a tester digest alone cannot identify changed plugin
content, and a registry's update policy is distinct from the existence of its callback.

## Executed regression

At core baseline `ac7617466088b3e43a2a90b893218e0cf21de481`, the actual extracted POST step
sent a synthetic workflow SHA `111…` when the content checkout was `222…`. Both controls failed
at that payload assertion: core building Plugins, and a caller using a different explicit
content ref in its own repository. No network request was made: the fixture captures the
actual `curl` arguments and executes the real JSON construction and HMAC signing.

`PluginPublicationProvenanceTest` also executes the actual image-version step against supplied
Docker inspection results. The 20 cases cover both corrected payloads, missing/malformed
producer outputs before any POST, valid/missing/duplicate/malformed or unreadable image
configuration, and the exact job-output wiring. They passed with 52 related publication,
signature and embedded-document checks: **72 passed, zero failed or skipped**. The strict
Documentation.Test build reported zero warnings/errors. Workflow shell checks and their
self-test, actionlint, YAML key checks (34 workflows), permission pairing (two callers) and
timeout checks (84 jobs) also passed.

The local before/after receipts are under
`/private/tmp/plugin-publication-sender-receipt`; they are supplementary. Reproduce the
committed assertions from a fresh build:

```bash
dotnet build test/MeshWeaver.Documentation.Test/MeshWeaver.Documentation.Test.csproj -c Release -warnaserror
dotnet test test/MeshWeaver.Documentation.Test/MeshWeaver.Documentation.Test.csproj -c Release --no-build --filter 'FullyQualifiedName~PluginPublicationProvenanceTest|FullyQualifiedName~UpstreamBuildGateGuard|FullyQualifiedName~InboxSignatureVerdictGuard|FullyQualifiedName~PlatformReleaseNotifyGuard|FullyQualifiedName~PlatformBakeLaneGuard|FullyQualifiedName~WhatsNewEntryIntegrityTest|FullyQualifiedName~DocumentationEmbedIntegrityTest'
```

No live notifier, release workflow, registry registration or adoption was changed to execute
these tests. Production delivery requires the normal reviewed shared-lane release and a
subsequent successful publication using it.
