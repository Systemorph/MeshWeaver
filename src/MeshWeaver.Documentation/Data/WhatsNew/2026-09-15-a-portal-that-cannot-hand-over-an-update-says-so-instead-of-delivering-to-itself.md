---
Name: A portal that cannot hand over an update says so instead of delivering to itself
Category: Fix
Description: A portal that hands platform updates to a control instance but has no signing secret now reports detect-only and names the missing key, instead of quietly storing the release in its own inbox and reporting a successful hand-over. Portals that still apply updates themselves are unaffected.
Icon: ArrowSyncCheckmark
Order: -20260915
---

# A portal that cannot hand over an update says so instead of delivering to itself

Portals are moving off applying platform updates themselves. **Where that has happened** — where the
portal is no longer permitted to change its own workloads — a release it spots is **handed over** to
the control instance instead, which opens a `Roll` for a person to approve. The hand-over is one
signed message, and the signature needs a secret the operator mounts on that portal.

*A portal that still applies its own updates is unaffected by everything below: it keeps doing
exactly that.*

A portal on the new footing that has not been given that secret cannot hand anything over. It
should say so, by name, in its start-up line:

> `apply=detect-only (no control inbox: Hosting:ReportTo names the control instance but
> Hosting:ControlInbox:Secret is empty, so nothing could be signed)`

**Some portals reported the opposite.** A portal that both names a control instance *and* runs an
inbox of its own — the build portal does, because it is where the fleet's builds are queued — fell
through to that second inbox when the signing secret was missing. It delivered the release to
**itself**, its own watcher saw a message it had no use for and deleted it, and the start-up line
read as a completed hand-over:

> `apply=control-lane (a detected release is handed to this instance's own Hosting/PlatformBuilds
> inbox; the control plane rolls)`

Nothing was rolled, nothing was queued for approval, and nothing said a key was missing. The release
was simply gone — and the line an operator would read to check the transition was the line that
looked fine.

**Naming a control instance is now exclusive.** A portal that names one is a *consumer* of it,
whatever else it also runs: with the secret it posts the release there, and without the secret it
refuses and names `Hosting:ControlInbox:Secret`. Its own inbox is never the destination. Delivering
in-process stays what it always was — the route the control instance itself takes, because that
instance names no control instance above it.

If you operate a portal in this state, the start-up line and the **Updates** tab now agree with
reality: it is detect-only until the secret is mounted, and it tells you which key to mount.
