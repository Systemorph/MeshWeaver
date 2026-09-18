---
Name: Saving a page no longer reads it as the wrong kind of content
Category: Fix
Description: When a save proposed a different kind of content than the page already held, the checks that run before the save were shown a made-up version of the page — and a harmless save was reported in the logs as a failure.
Icon: Bug
Order: -20260918
---

# Saving a page no longer reads it as the wrong kind of content

Every page holds **content of a particular kind** — a mail draft, a markdown document, a form
record. Before a save is applied, a set of checks compares what is being saved against what the page
already holds, so that things like "this would overwrite a newer version" can be refused.

For that comparison to work, both sides have to be read as the same kind of content. The save
therefore said: *read the stored page as whatever kind the save is proposing*. That is right almost
always — the two are the same kind — and it was already handled when a save deliberately **changes**
a page's type.

It was not handled when the kinds differ while the page's type stays the same, which does happen:
an app saves a markdown body over a page whose type belongs to a plugin, or an import falls back to
reading a file as plain markdown because it could not read the type it declared.

**In that case the stored page was re-read as the proposed kind anyway — and that reading succeeds.**
Unknown fields are dropped and missing ones are filled with blanks, so the checks were handed a
tidy, plausible page that never existed: some of the old values under new field names, everything
else empty. A check comparing the two then decided on that invention.

**The save now asks first.** Stored content says what kind it is, and when that disagrees with what
the save proposes, the stored page is left exactly as it is instead of being re-read as something
else. The checks see the real page.

The same fix removes a misleading error. When the two kinds could not be read as one another at all,
the logs recorded it as a failed conversion — so a perfectly good save of a mail draft was filed as
an incident, repeatedly, while nothing was actually wrong. That case is now reported for what it is:
a note that the page and the save carry different kinds of content, so the comparison checks will
not apply.

Nothing needs to be done, and no save that worked before is refused now.
