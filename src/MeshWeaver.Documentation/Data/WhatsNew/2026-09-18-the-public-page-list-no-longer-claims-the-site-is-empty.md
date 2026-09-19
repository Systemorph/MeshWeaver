---
Name: The public page list no longer claims the site is empty when it could not read it
Category: Fix
Description: Nine times in a day, the file search engines read to find this portal's public pages was served complete, correct — and declaring that nothing here is public, while 1,819 pages were live. It happened whenever the list could not be built, because "I could not check" and "I checked, there is nothing" produced the same answer; they are now different answers, and the second one says so.
Icon: Globe
Order: -20260918
---

# The public page list no longer claims the site is empty when it could not read it

Every site hands search engines a `sitemap.xml`: the list of pages a visitor who is not signed in
may open. Ours is not a file anybody maintains — it is built on the spot, by asking which top-level
spaces are public and then listing the readable pages inside each one.

An automated probe checks it every few minutes. Over one day it caught the portal serving this **9
times out of 100**: a perfectly well-formed list, no error, no timeout — **containing nothing at
all.** Measured in the same minute, the very same address served **1,819 pages**.

## Why an empty answer was the wrong one

Asking whether a space is public has three possible answers, not two: *yes*, *no*, and **"I could
not work it out"** — the last one for the seconds when the permission machinery is briefly
unavailable. The page list only ever asked for *yes* or *no*, and treated the third answer as *no*.

For a single page that is a sound trade: leaving one out of a list of a thousand costs nothing much,
and the file was never a promise of completeness. **But when it happens to every space at once, the
file that comes out is no longer a shorter list — it is the opposite claim.** A search engine
reading it is told, in a document that looks entirely healthy, that this portal publishes nothing.

Nothing else had gone wrong. No page had been unpublished, no permission had changed, and by the
time anybody looked the list was correct again — which is exactly why it went unnoticed until a
probe that refuses to pass on an empty sample caught it.

## What it does now

The page list keeps the reason it could not decide, instead of throwing it away, and one rule
decides what happens next:

- **Pages found** → published, exactly as before. Even if one space could not be decided: a list
  missing one entry is still a useful list, and the missing page is back on the next visit.
- **No pages, and the portal genuinely publishes nothing** → published as an empty list. That is
  the truth, and a private deployment is entitled to say it.
- **No pages, and something could not be checked** → the portal answers *"temporarily unavailable,
  come back shortly"*, which is the standard signal a search engine already understands, instead of
  a confident list of nothing.

Only the last case changed, and only in the one situation where the old answer was a false
statement rather than a smaller one.

## Now it also says what went wrong

The deeper problem was that none of this left a trace. Three different failures — the permission
check giving no verdict, the lookup taking too long, the query itself failing — were all quietly
turned into "no pages", with the reason discarded. Nine occurrences in 27 hours produced **not one
line** anybody could read, which is why the fault could be measured precisely and explained not at
all. Each of the three now records what happened, by name.

The same list is what the **Published to the Web** settings tab shows an administrator, built from
the same code so the two cannot disagree. It had the same flaw — an empty table reading as "nothing
here is public" — and now says the surface could not be read, in English and German.

The full account, including which cause the logs ruled out and which remain open, is in
**Doc → Architecture → A Zero-Root Sitemap Is an Assertion**.
