---
Name: A refusal now says which hub refused
Category: Fix
Description: When a part of the mesh is restarting, the refusal it sends back is now written in one place instead of six, so everything reading it can tell "this address is coming back, ask again" from "there is nothing there" — whichever part of the hub happened to answer.
Icon: ArrowSync
Order: -20260902
---

# A refusal now says which hub refused

A node's own little service can go away for a moment — a restart, a recycle, a piece of content
being republished. Anything that asked it something at that instant gets a refusal rather than an
answer, and the refusal has a job to do: it has to say **"this address is coming back — ask
again"**, so the page, the reader or the installer waiting on it re-asks a second later and gets the
real answer instead of reporting that the thing does not exist.

That message is written at the moment of refusal, and there are several moments where it can happen:
the request arrived after the service left duty, or it was already queued and its turn came too
late, or the permission check could not run because the service was already gone, or the work it
needed could no longer be created. **Each of those wrote its own sentence.** They said the same
thing, but not in the same words — and the words are what the rest of the system reads.

So the answer to "who refused, and is it coming back?" depended on which of those moments you
happened to hit. Most of the time it did not matter. When it did, the reader saw a refusal it did
not recognise, and treated a service that was two seconds from being back as one that was not there
at all.

**All of them now compose the sentence in one place**, and everything that reads a refusal reads it
from that same place. A new kind of refusal added later is understood the day it is written, rather
than the day someone notices a page went blank and traces it back.

Nothing you do changes. What changes is that a restart stays a pause instead of occasionally looking
like an absence — and that the next moment of refusal added to the platform cannot quietly reopen
the gap, because there is no second place left to write it.
