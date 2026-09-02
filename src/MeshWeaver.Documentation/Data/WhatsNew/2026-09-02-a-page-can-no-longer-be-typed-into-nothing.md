---
Name: A page can no longer be typed into nothing
Category: Fix
Description: Changing a node's type to one that does not exist used to be accepted, leaving a page that never loaded and never said why — it is now refused, and an update that deletes a type in use says which pages it affected.
Icon: Bug
Order: -20260902
---

# A page can no longer be typed into nothing

Every node names a **type**, and the type is what knows how to load and render it. If the type is
missing, the node has nothing to load it with: the page does not show an error, it simply never
finishes loading and comes up empty. Nothing on the screen — or in the logs — said which type had
gone missing, so the usual conclusion was that the content itself had been lost.

Two things could produce that state, and both are now closed.

**Changing a node's type to one that does not exist is refused.** Creating a node has always been
checked, but *changing* one was not, so an edit could quietly point a page at a type nothing knows
about. The refusal says which type is missing and what to do about it. Repairing a page that is
already in this state still works exactly as before — pointing it back at a type that does exist is
allowed, and is the way to fix one.

**Removing a type that is still in use now says what it affected.** When a package or a synced
repository stops shipping a type, that type is removed from the mesh — that is deliberate, and it
has not changed. What is new is that the update reports the pages that were still using it, by name,
so they can be moved to another type or removed instead of being discovered later as blank pages.
The update is marked as finished-with-warnings, and the warning names both the type and the content.

Nothing needs to be done to enable either. Existing pages in this state are unaffected until someone
repairs them; the update history for a package that removed a type in use now shows which content it
touched.
