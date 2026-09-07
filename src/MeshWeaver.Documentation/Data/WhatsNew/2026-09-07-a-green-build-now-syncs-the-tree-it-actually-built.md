---
Name: A green build now syncs the tree it actually built
Category: Fix
Description: A repository's green build used to bring every synced Space to the branch's CURRENT tip — which is a different tree whenever something merged while the build was running. It now imports the exact commit the build proved, so a portal can no longer receive sources that no build has ever compiled.
Icon: ArrowSyncCheckmark
Order: -20260907
---

# A green build now syncs the tree it actually built

A Space connected to a repository updates itself when that repository's CI goes green. The signal is
deliberately the **build**, not the merge: a push arrives before the build it starts, so importing on
the push would ship content ahead of the gate meant to vet it.

The build carries the commit it ran against. The import did not use it. It asked for *"update to
latest"* instead — which resolves the configured branch **at the moment the files are fetched**,
seconds to minutes after the build finished. Those are the same tree only while nothing merges in
between.

## What went wrong when something did merge

On 6 September a plugins repository run that started before a large change finished about twenty
minutes after that change had landed. Two portals took the webhook and imported the branch tip: a
tree whose new code needed a platform they were not running, and which their own CI had never
compiled against anything.

Four types in the Store — the catalog, the order/checkout path, the plugin page and maintenance —
went to a compile error and stayed there. A compile result is latched: nothing re-tries it until the
source changes, and the next sync would have delivered the same source. On the commercial portal
that meant customers could not buy or install for about five hours, and it took a platform roll to
clear it.

Nothing anywhere was red. The webhook answered `200`, the build record was written, the import
activity finished, and CI on both repositories was green. The only visible symptom was four node
types quietly serving an error.

## What changes

A build-triggered import now fetches **the build's own commit**. Concretely:

- the Space receives exactly the tree that CI proved green;
- the "already at this commit" check that skips redundant imports now compares like with like — what
  a source records is the commit it was told to fetch;
- the Space's activity log names the commit, in English and German, so an operator can see *which*
  tree landed without leaving the page.

Clicking **Update to latest** yourself is unchanged. Asking for latest and getting latest is correct
when a person is asking and is there to see the result; it is not correct for an unattended trigger,
where nothing on the instance authorised the tree that would arrive.

Unattended imports also no longer have a branch fallback at all: a caller that cannot name a proven
commit now fails, loudly, instead of quietly reverting to the behaviour above. "We could not
establish which commit" and "the branch tip" are different answers, and treating the first as the
second is what turned an unread value into a confident wrong one.

## What this does not change

Pinning the commit guarantees the tree was proved green *by its own repository's CI*. It does not
guarantee your portal runs a platform new enough for it — that is a separate question about what a
publication contains and which instances may adopt it, and it is tracked separately.

The full contract, the measured timeline and the one import path that still reads a branch tip are in
[The Sync-Ref Contract](/Doc/Architecture/SyncRefContract).
