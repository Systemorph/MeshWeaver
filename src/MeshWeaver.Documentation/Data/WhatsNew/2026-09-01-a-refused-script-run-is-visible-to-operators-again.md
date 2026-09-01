---
Name: A refused script run is visible to operators again
Category: Fix
Description: When ExecuteScript refuses a target up front, the portal now writes a warning naming the path and the reason — so a run that never started leaves a trace an operator can find, not just a reply the caller alone can see.
Icon: Bug
Order: -20260901
---

# A refused script run is visible to operators again

Two things are supposed to happen when a script run is refused: the **caller** is told why, and the
**operator** gets a trace. Both matter, and for different people — the caller can fix a mistyped
path, but only the log tells whoever is looking after the deployment that a run was asked for and
never happened.

The up-front check that now answers `NodeNotFound` and `NotExecutable` immediately, instead of
waiting out the request budget, only carried the first half over. The node's own hub had always
written a warning before refusing; the new check answers before the request ever reaches that hub,
so from the moment it landed the two commonest refusals produced a reply for the caller and
**nothing at all** in the log — the exact silence the original report was about.

The check now writes that warning itself, naming the path, the condition and the fact that no
activity node was created:

```
ExecuteScript refused for Acme/Reports/Monthly (NotExecutable): Not executable:
'Acme/Reports/Monthly' has CodeConfiguration.IsExecutable = false. Set it to true on the
Code node to make it runnable. No Activity node was created.
```

It sits on the single place every up-front refusal passes through, so a refusal added later cannot
be silent by omission — the same shape the hub-side refusal already uses. Nothing about what the
caller receives has changed.
