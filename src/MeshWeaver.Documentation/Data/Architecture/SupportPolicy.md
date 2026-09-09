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
Any later cleanup must still respect deployment pins and other active references.

## Operational logs

Operational logs are retained for 30 days and cleaned up automatically by age. This
log-retention window does not apply to official release artifacts. Persistent storage
and regular cleanup serve different purposes: storage preserves logs across restarts,
while retention removes logs after their agreed history window.

See [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) for promotion and
[Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) for deployment behavior.
