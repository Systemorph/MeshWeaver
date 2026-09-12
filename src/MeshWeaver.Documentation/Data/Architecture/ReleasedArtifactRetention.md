---
Name: Released Artifact Retention
Category: Architecture
Description: Retain what released sets and their consumers need while cleaning unused continuous artifacts after 30 days.
---

# Released Artifact Retention

The release contract in #3842 governs retention: module builds resolve a released,
sealed platform at run time; compatibility follows declared major versions; consumers
see the last green publication; and portals announce adoption. Retention must preserve
that behavior through cleanup. It must not reintroduce platform pins to make an
inventory easier to compute.

## Protection follows use

The publisher identifies the last green set it actually advertises per served major
and channel. A successful source build alone is insufficient: the set must be sealed
and released. Failed, cancelled and unfinished publications cannot displace it.

Deployment reports identify the versions actually serving, including the adopted
same-major fallback. Build records identify the released platform resolved for an
active run. Official release records identify supported versions and their underlying
.NET lifecycle. These records identify exact artifacts for storage protection; they
are not exact-identity compatibility gates.

Expand every protected record to its artifact closure across the registry and bundle
stores. Include the images and referenced manifests, bundles, release markers and
restorable source version. A tag retained without its referenced content is a broken
release, not a retained release. Follow declared dependencies across versions rather
than constructing a closure from identical tag names.

## Publication and cleanup ordering

Protection must be established and verified before a set is advertised or a build
starts consuming it. Publication, consumption registration and cleanup must share an
ordering mechanism that prevents a new reference racing with deletion. Separate nightly
lock and purge timers do not establish this ordering.

A new publication does not retire the old one until the publisher no longer advertises
it and consumers no longer need it. A readiness notification does not acknowledge
adoption by every portal. Missing or stale consumer inventory must prevent deletion;
it must never silently count as zero consumers. Failed and cancelled builds release
only their unused consumption records after terminal-state reconciliation.

Unreferenced continuous artifacts become eligible after 30 days. There is no retained
build-count quota. A last green set may therefore survive much longer than 30 days
when newer builds fail. A serving fallback also survives regardless of age.

Official releases and their closure remain available throughout support. MeshWeaver
major support follows the underlying .NET release. Plugin support additionally depends
on the supported collaboration with its counterparty. Unknown support mappings are
retained until resolved. End of support alone does not delete a still-consumed release.

## Bundle-store implementation

The portal bundle sweep uses a minimum 30-day age window, keeps adoption stamps and
reported consumer identities, and protects the latest sealed publication per source
for every represented major. An unresolved consumer version aborts the sweep.
`PreWarm:PrebuiltBundleRetention:MinimumAge` may extend the window; values below 30 days
cannot shorten it. The legacy `KeepNewestPerSource` member and configuration-key
constant remain for binary/source compatibility but no longer control cleanup.

This is not yet proof of the complete publisher/consumer coordination below. A sealed
bundle is a local publication signal; the registry's last-green advertised set and
active builds must still participate in the common protection inventory.

## Transition from the pin scanner

The existing `lock-pinned-digests.py` scans workflow pins and deployment overlays and
protects official image tags. Its zero-pin assertion is a historical denominator check
and must be replaced by positive evidence that the new inventories were read
completely. Removing that assertion alone would not establish protection.

**Done, 2026-09-12** — see [ArtifactRetentionInterlock](/Doc/Architecture/ArtifactRetentionInterlock).
The scanner now also inventories every running portal: each installation the deployment
overlays declare is asked, through its own `/api/version`, what it is RUNNING, and the
closure of image sets built from that commit is protected. The zero-pin assertion is
replaced by a positive control on the extractor itself, run against a fixture on every
pass — which fires even when the fleet happens to declare pins, where the fleet-shaped
assertion it replaces expired the day #3842 moved the satellites to run-time resolution.
An installation that does not answer is named, makes the run INCOMPLETE and refuses the
unlock arm; a deliberately non-live installation is declared in
`.github/acr-retention/instances.json` with a reason, so silence is never read as
retirement. Released module bundles and active builds are still NOT inventoried.

A retained release also needs its REFERENCE, not only its content. A container tag
carries its own deletion attribute, and it is the one the purge reads when deleting a
tag, so a protected manifest under an unprotected tag is exactly the broken release
described above with the halves reversed: the bytes survive and the name stops
resolving. Both are locked.

Keep existing protection while consumers migrate. Once the released-set and consumer
inventories cover the fleet, their coverage replaces source-pin counts; do not restore
pins or add a pin freshness check. Only reconcile old locks against that complete
inventory. No pin removal, green build or major-version comparison authorizes an unlock
on its own.

The files under `.github/acr-retention/` record the cloud tasks last read; they are not
automatically deployed. The recorded 7-day/count-based CI cleanup is not the desired
30-day policy. Update the live task only with the coordinated protection path ready,
a reviewed report-only deletion list, and verification that the superseded task stays
disabled. Verify the actual purge-tool version enforces age for untagged artifacts and
preserves referenced manifests. Never include locked manifests in a purge.

## Acceptance evidence

- A last green release older than 30 days survives repeated red and cancelled successors.
- An older same-major bundle still serving on a portal survives a newer publication.
- A build resolving a released set concurrently with cleanup cannot consume deleted bytes.
- Removing every module platform pin leaves complete release/consumer coverage and succeeds;
  an unreadable inventory fails and permits no deletion.
- Missing closure members block cleanup; keeping a marker alone never passes.
- Unreferenced continuous artifacts older than 30 days are eligible; younger artifacts
  and supported official releases remain protected, without a count quota.
- A declared major incompatibility affects adoption; it does not delete the old major
  while that major remains supported or consumed.
- Terminal-build and completed-adoption reconciliation releases only unused references.
- The live cleanup and publication use the same ordering mechanism, with no independent
  purge timer capable of bypassing it.

This is the target contract. The transition is complete only when the publication and
consumer producers, retention reader and live cleanup satisfy these checks. Policy
text and a successful legacy pin scan do not prove that cutover happened.

See [Release Support Policy](/Doc/Architecture/SupportPolicy).
