---
Name: Release Support Policy
Category: Architecture
Description: MeshWeaver major-version support follows the lifecycle of its underlying .NET release.
---

# Release Support Policy

Each MeshWeaver major release is supported for the remaining support lifetime of the
.NET release on which it is based. Support ends when Microsoft ends support for that
underlying .NET release. Shipping a later MeshWeaver minor or patch release does not
restart or extend the major version's support period.

The authoritative .NET end-of-support dates are published in
[Microsoft's .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
Record the underlying .NET release and its end-of-support date in each MeshWeaver major
release's notes. If Microsoft changes that date, update the recorded date to match.
A new MeshWeaver major release does not end support for an older major while its
underlying .NET release remains supported.

## Plugins and connectors

A plugin is supported while both of these conditions hold:

- The underlying MeshWeaver release is supported.
- The collaboration with the relevant counterparty remains supported, including the
  data source, third-party service or other external integration on which it depends.

Plugin support ends when either condition ceases to hold. A supported MeshWeaver
release alone does not extend the lifetime of an integration its counterparty no
longer supports.

MeshWeaver aims to support a broad variety of connectors. Each connector's
documentation should identify its supported MeshWeaver releases and the external
services or data sources on which its support depends.

## Official release retention

Keep official MeshWeaver releases available throughout their support period. This
includes their container images, sealed bundles, manifests and other artifacts needed
to install or restore that release. Keeping a version tag while deleting a referenced
digest does not satisfy this policy.

Regular cleanup must exclude supported official releases and the artifacts they
reference. Their retention is determined by support status, not a rolling age limit or
a count of newer releases. An unknown runtime mapping or support date is not evidence
that a release is unsupported; resolve it before deleting its artifacts.

End of support ends this retention guarantee; it does not itself issue a deletion.
Any later cleanup must still respect running deployments, active builds and other active references.

## Continuous build artifacts

Use regular age-based cleanup for continuous build artifacts, with a 30-day retention
window rather than a quota of newer builds. Age alone never makes an artifact eligible
while it is still needed by a published release, a running deployment or an active build.

Module repositories resolve the released, sealed platform at build time; they do not
carry a platform pin or a staleness gate. Retention must therefore derive protection
from publication and consumption records, not require pins to exist in source control.
Compatibility follows declared major versions: a same-major adopted module can keep
serving while its successor is pending. An exact build identity remains useful for
locating and retaining bytes; it must not become a compatibility refusal.

Protect the complete artifact closure of:

- The last green released set available to consumers for each served major/channel.
  A red, cancelled or merely unsealed newer build does not replace that set.
- The versions actually adopted by running portals, including a same-major fallback
  still serving while a replacement is being prepared.
- The released set resolved by an active build, and a publication being prepared for
  exposure to consumers.
- Supported official releases, regardless of whether a portal currently uses them.

The closure includes container manifests and their referenced images, sealed module
bundles, release records and the sources needed to install or restore the release.
Resolve it from the publication records rather than assuming all dependencies have
matching version strings. Never keep a tag or marker while deleting the bytes it names.

Protect a set before advertising it or letting a build consume it. Reconcile these
references with cleanup under one ordering contract so a new reference cannot appear
between the cleanup inventory and deletion. Keep the previous set protected until it
is no longer advertised or consumed; publishing a successor is not evidence that every
portal adopted it. Announcing readiness is a notification, not proof that the old
version is unused.

An unavailable deployment report, incomplete publication inventory or unresolved
artifact prevents deletion. Explicitly reported zero CI pins is valid after migration;
it is neither evidence of an empty protected set nor permission to unlock old images.
Legacy references remain protected during migration until their consumers are accounted
for. See [Released Artifact Retention](/Doc/Architecture/ReleasedArtifactRetention) for
the transition and its verification requirements.

The image-protection script treats clean version tags in the image repositories
promoted by MeshWeaver's official release workflow as official releases, even when
no current deployment pins them. Independently versioned helpers and cached third-party
images are not classified as MeshWeaver releases merely because their tags are numeric. Until their support end is established, they remain
protected and are not candidates for the optional unpinning cleanup.

The existing cloud cleanup schedule must be brought into line with this policy;
this policy alone does not change its settings. Protection must succeed before cleanup
runs, and the cleanup tool must enforce the age window for untagged artifacts too.

## Operational logs

Operational logs are retained for 30 days and cleaned up automatically by age. This
log-retention window does not apply to official release artifacts. Persistent storage
and regular cleanup serve different purposes: storage preserves logs across restarts,
while retention removes logs after their agreed history window.

See [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) for promotion and
[Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) for deployment behavior.
