---
Name: Something busy no longer reads as something gone
Category: Fix
Description: Two ways the platform could report "this doesn't exist" when it actually meant "I couldn't look right now" — one of which let a replica re-create content you had just deleted.
Icon: Sparkle
Order: -20260814
---

# Something busy no longer reads as something gone

When you ask the platform for a page, a note or any other item, there are several honest answers.
"Here it is." "There is nothing here." "It is being deleted right now." "I could not reach it just
this second." Until now the last three all came back looking identical — as *nothing here* — and
that turns a moment's disturbance into a statement of fact.

Two consequences, both fixed.

**A page being restarted no longer reports itself as missing.** Parts of the platform recycle
routinely: after an update, after an edit that needs recompiling, whenever a page is moved between
machines. Ask for something in the second or two while its owner is winding down, and the answer
used to be "this does not exist" — with the real reason ("shutting down, ask again") replaced by an
unreadable internal message. Anything that would have simply waited and retried instead concluded
the item was gone. The recycle window now answers honestly, so the retry that was already built in
actually happens and you get your content.

**Replicated spaces no longer resurrect something you deleted.** When a space is mirrored to another
instance, the mirror periodically compares both sides. If you deleted an item and the mirror looked
in the moment between "delete accepted" and "delete finished", it saw *nothing here* and helpfully
put the item back — from the copy the other side had not caught up on yet. Worse, the copy that came
back could mask the deletion so it never reached the other instance at all, leaving the two sides
permanently disagreeing. The mirror now recognises a delete in progress and stays out of its way,
and it will no longer treat a failed lookup as evidence that something is absent — so a brief
network hiccup can neither overwrite your newer content with an older copy nor delete the far side's.

Nothing you can see changes when everything is healthy. What changes is what happens in the
seconds when it is not.
