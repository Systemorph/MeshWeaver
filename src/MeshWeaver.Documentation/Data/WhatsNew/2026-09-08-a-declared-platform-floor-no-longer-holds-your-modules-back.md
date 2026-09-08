---
Name: A declared platform floor no longer holds your modules back
Category: Fix
Description: Every installed module declares the platform version its author expects. That declaration used to be a gate — an update was declined, a module was refused or skipped — whenever the version strings ranked the wrong way, even when the module would have loaded fine. It is now an advisory; whether a module loads is measured.
Icon: ArrowSync
Order: -20260908
---

# A declared platform floor no longer holds your modules back

Every module package declares the platform version its author expects it to need
(`minMeshVersion`). Until now that declaration was compared with the running platform's version
at eight different places, and a mismatch stopped things: the self-updater declined a release, a
module bundle was refused before it reached the disk, a landed module was skipped at boot, and the
status pages reported it as "held above this platform".

The comparison was a **string order**, not a compatibility check — it ranks a continuous build
(`3.0.0-ci.8055`) below a release candidate (`3.0.0-rc8`), and a release candidate below the
release. On 7 September every production portal spent the day on its morning build because of it:
the installed modules declared release-candidate floors, the candidate releases were all continuous
builds, and the updater declined every one of them ("77 plugins required … every one declined") —
while each of those modules would have loaded without a problem.

The declaration is now **advisory**. It still appears in the log and on the module's status row as
*"declares platform ≥ X; this installation runs Y"*, so you can see what a module claims, but it
decides nothing. Whether a module actually loads is
[measured](/Doc/Architecture/ModulePlatformLinkGate): the platform links the module's bytes against
what it really has before landing them and again at boot, and refuses only what genuinely cannot
load — naming the missing type. A floor that is set too high is still caught where it is written,
when the module is packed.
