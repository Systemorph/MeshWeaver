---
Name: A stuck sync stops re-downloading its repository
Category: Fix
Description: A GitHub-synced Space that cannot finish an import no longer re-downloads the whole repository on every green build of that repository — and the settings tab now says why it has stopped trying.
Icon: ArrowSync
Order: -20260910
---

A synced Space imports whenever the repository it tracks builds green. That delivery was free only
when the Space had already fully reached the built commit — and a Space that *cannot* fully reach
it (a node kept back because it was edited here, a file the mesh refuses) was, by exactly that
rule, the one that never qualified. So it downloaded the entire repository again on every green
build, at the repository's build cadence rather than at any rate of its own. On one live Space that
had been running for a month; on a busy repository it is roughly ten downloads an hour, most of
them for a commit that had already been looked at.

Now the source remembers **which commit it last looked at** separately from **which commit it has
actually got**. When the last attempt ended in a verdict that reading the same files again cannot
change, later builds of that same commit are skipped outright — no download, no re-import. The next
new commit still brings a full import, and **Update to latest** still works exactly as before.

Sync sources that are healthy are unaffected: they were already skipping those deliveries.

Two things a Space admin will notice on the **GitHub Sync** settings tab. Repeated import activity
records stop piling up for a Space that is stuck. And when a source has settled, the line under the
repository now reads *"this commit has a final verdict — the next new commit re-attempts"*, so a
last-run time that has stopped moving reads as a deliberate skip rather than as a delivery that
never arrived.

Nothing was made less careful. A failure that might not happen again — a store that was briefly
unreachable, an import that faulted outright — is still retried on the very next delivery, because
freezing a Space out over a passing problem is the more expensive mistake. Only a verdict about the
files themselves settles.
