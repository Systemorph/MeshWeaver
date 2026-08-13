---
Name: A long answer no longer costs the portal more than it says
Category: Fix
Description: While an agent typed out a reply, the portal re-sent the entire answer-so-far on every refresh — so a long answer cost megabytes and slowed everything sharing the machine. It now sends only the new words.
Icon: Sparkle
Order: -20260813
---

# A long answer no longer costs the portal more than it says

When an agent writes you a reply, the text appears a few words at a time. Behind that, roughly ten
times a second, the portal saved the answer so far so that the page you are watching — and anyone
else watching the same conversation — stays up to date.

The problem was *what* it saved. Every one of those saves carried the **whole answer from the
beginning**, not just the words that had arrived since the last one. Early in a reply that costs
nothing. By the end of a long one it means shipping the entire text again, and again, and again. A
page-length answer ended up moving several megabytes through the portal to deliver twenty kilobytes
of writing — and the cost grew with the *square* of the answer's length, so an answer twice as long
was four times as expensive.

Nobody saw a wrong word on screen; the text was always correct. What people saw was everything
*else* getting slower while a long reply was in progress. Handling those repeated copies is real
work for the machine, and it competes with every other thing the portal is doing at that moment —
other people's pages, background jobs, the health checks that decide whether the service is
answering at all.

Now a save carries only the part that changed. The portal already has the earlier text; it does not
need to be told again. In a measured run, a twenty-thousand-character answer went from **3.8 MB of
saved updates to 0.07 MB** — about fifty-five times less — and the cost now grows in step with the
answer's length instead of racing ahead of it.

Two things are deliberately unchanged. The finished text is identical to the character — this is a
change in how the update travels, never in what it says. And if two things somehow write to the same
text at the same moment, the portal refuses the second one and has it start again from the current
text rather than guessing where the new words belong; correctness wins over saving a round trip.

The same saving applies anywhere the portal stores a large piece of text that grows or is edited in
place — long documents, generated pages, notes — not just agent replies.
