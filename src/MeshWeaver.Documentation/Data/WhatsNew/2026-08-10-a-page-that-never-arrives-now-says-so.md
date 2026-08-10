---
Name: A page that never arrives now says so
Category: Fix
Description: An area that produced neither content nor an error left a spinner on screen indefinitely and wrote nothing to the log, so nobody could tell which part of the page was stuck.
Icon: Sparkle
Order: -20260810
---

# A page that never arrives now says so

Each part of a page is built by its own small piece of code. While that runs, the spot
shows a loading label — *"Rendering Overview… awaiting first data"*. When the content
arrives, the label is replaced. When the code fails, the failure is shown in its place.

There was a third outcome nobody had accounted for: the code produced **neither**. No
content, no error — just an ordinary finish, or a quiet wait that never ended. Nothing is
put on a clock there, deliberately: a page that legitimately takes a while must never be
cut off halfway. So the spot kept its loading label, and — because nothing had gone wrong
in any way the platform recognised — **nothing was written to the log either**. The page
looked busy forever, and the record of what it was busy with was empty.

That combination is what made a recurring test failure impossible to diagnose: the page
was demonstrably alive and exchanging data the whole time, one region of it simply never
appeared, and there was nothing anywhere naming which region or why.

The platform now keeps count of what each region actually delivered, and says so at the
two moments it can know for certain: when the region's work reports itself finished
having produced nothing, and when the region is torn down having never produced anything.
Both name the region and the page it belongs to.

Nothing about rendering changes — nothing is cut short, retried, or turned into an error
that wasn't one. Slow pages are still allowed to be slow. What changes is that a region
which never arrives is no longer invisible.
