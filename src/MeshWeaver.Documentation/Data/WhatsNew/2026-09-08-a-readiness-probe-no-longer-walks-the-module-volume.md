---
Name: A new pod becomes ready again on a large module volume
Category: Fix
Description: The module-activation health check now reads the shared module volume once per change instead of on every probe, so a fresh pod on a deployment with hundreds of landed module generations passes its startup probe instead of timing out forever.
Icon: HeartPulse
Order: -20260908
---

# A new pod becomes ready again on a large module volume

Every startup and readiness probe asks the portal whether modules have landed that this process has not
loaded yet. Until now that question was answered by walking the shared module volume: every landed
module's record was re-read and every landed assembly's existence re-checked, on every probe. On the
public instance, with more than seven hundred module generations on a network volume, one answer took
eight to ten seconds against a five-second probe timeout, so a freshly started pod never became ready
and no rollout could complete, whatever image it carried.

The volume is now read once per change. Every writer lands its files by renaming into one of three
directories, so those directories' timestamps say whether anything moved; a probe costs three
timestamp reads and reuses the last snapshot until they change. What this process has loaded is still
measured fresh on every call, so a module that loads is reported as loaded without touching the volume.
