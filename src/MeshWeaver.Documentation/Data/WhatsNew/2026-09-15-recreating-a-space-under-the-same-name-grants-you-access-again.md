---
Name: Re-creating a space under the same name gives you access to it again
Category: Fix
Description: Deleting a space and creating a new one with the same name sometimes left you unable to add anything to it — "Create permission required" — even though the new space was yours. Deleting a space now clears the access cache that belonged to it, so the new space is judged on its own grants.
Icon: ShieldKeyhole
Order: -20260915
---

# Re-creating a space under the same name gives you access to it again

Deleting a space and then creating a new one with the **same name** could leave you locked out of
your own space: the space was created, it said it was yours, and the first page you tried to add to
it was refused with *"Access denied: Create permission required"*. Waiting and trying again worked,
which is what made it so hard to pin down.

Behind every permission decision on a space there is one cached list of that space's grants, shared
across the whole portal. Deleting a space removes its database entirely — but the cached list of its
grants was left behind. A new space created under the same name was then judged against the *old*
space's list, which had just been emptied, and it only learned about your new ownership grant a
moment later, when the change reached it. If your next write arrived first, it was refused.

**Deleting a space now clears the cached lists that belonged to it**, along with the live database
subscriptions behind them. The next decision about that name is made from the grants the new space
actually has — the same way a space created for the first time has always worked. Grants that belong
to the whole portal rather than to one space are untouched.

Nothing to do; the fix applies to every space, user home and other partition root.
