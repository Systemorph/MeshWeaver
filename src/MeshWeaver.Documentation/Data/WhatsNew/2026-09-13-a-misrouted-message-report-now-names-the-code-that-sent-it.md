---
Name: A misrouted-message report now names the code that sent it
Category: Feature
Description: When something addresses work at the mesh's router instead of a hub of its own, the platform now logs the call site alongside the two addresses — so the report identifies the caller instead of only the symptom.
Icon: Checkmark
Order: -20260913
---

# A misrouted-message report now names the code that sent it

Inside an installation, one hub is the **router**: its whole job is to pass messages between all the
others. Work must never be handed to it directly — when it is, the router spends its time doing that
work instead of routing, and under load the installation stops answering.

The platform already detects this and writes a report. Until now that report said *what* was
misaddressed and *between which two addresses* — and nothing about which piece of code had sent it.

## Why that was not enough

The detection happens on the hub that RECEIVES the misaddressed message, which is the wrong end to
ask. By then the message has left its sender, and if it crossed a machine boundary on the way it has
even lost its own type name.

The cost of that gap is measurable. One such report on our own installation was opened four separate
times over a month and ran to more than forty thousand log lines, and each investigation ended in the
same place: two addresses, several possible senders, and no way to tell which one it was.

## What changes

**The report is now also written where the message is created, and that copy carries the sending
call stack.** The first line of it is the code to fix.

Both reports are kept. The one on the receiving side is still the only one that can see a message
sent by a different process, so it keeps its job; the new one answers the question the old one could
not.

## What this does not change

**Nothing is blocked or rerouted.** This is a report, exactly as before — a misaddressed message is
still delivered and still works. The point is that it now says who to talk to.

**The volume stays where it was.** Each report is written once per kind of message, per hub — and the
new copy is written by the one hub that did the sending, so it adds a couple of lines to a log rather
than a copy of everything.
