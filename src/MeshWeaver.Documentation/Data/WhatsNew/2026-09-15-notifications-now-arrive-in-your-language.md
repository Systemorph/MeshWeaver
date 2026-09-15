---
Name: Notification emails arrive in your language, and the bell is ready to follow
Category: Fix
Description: Every notification was written as a finished English sentence, whatever language you read the portal in. Notification emails now arrive in the recipient's own language — subject, body, button and footer — and notifications themselves now carry what they mean rather than English text, so the bell renders each row in the reader's language as soon as the portal update carrying the new bell lands.
Icon: Mail
Order: -20260915
---

# Notification emails arrive in your language, and the bell is ready to follow

The portal renders its buttons, menus and settings in your language — but **notifications** did not.
Every row in the bell was English: *"Update available: …"*, *"Startup import failed: …"*, *"You've
been given access to …"*, and so was the email each one sent.

## Why it happened, and why it was not simply an overlooked string

A notification is written by the portal working in the background — a plugin checking for updates, a
module scan, an import running at startup, a page someone was granted access to. **Nobody is looking
at the screen at that moment**, so there is no language to write it in; the portal picked its own
default, English, and stored that finished sentence on the notification.

That is also why a handful of these notifications *looked* translated and still were not: the German
text existed, it was simply chosen at the moment of writing, when the only answer available was
"English".

## What you get today

**Notification emails follow the recipient.** An email has exactly one reader, and the portal knows
who — so the subject, the body, the button and both footer lines are now written in **that person's**
language rather than the server's. This is live with this update.

**Notifications now carry what they mean.** Instead of a finished English sentence, a notification
stores which message it is and the details that belong in it — *which* module, *which* partition,
*how many* errors. That covers the reminders you actually see:

- plugin and module updates — available, held back, or needing a global administrator
- a module discovered, added, refused or no longer offered by its repository
- a plugin registry that could not be reached during startup
- an import that failed at startup, and a page type that failed to compile or is serving a
  placeholder
- someone giving you access to a page or space

## What follows

The **bell itself** puts those pieces together into a sentence, and it picks up the new information
with the portal update that follows this one. Until then every row still shows the English it was
written with — nothing is missing or broken, it is simply not translated yet.

## What stays in English

Text the portal did not write itself: an error message from a compiler, an exception quoted from
another system, the raw startup log lines that a report carries. Those are shown exactly as they
arrived, in every language, which is more honest than a half-translation. Where such a fragment sits
inside a sentence the portal *did* write, the sentence around it is translated and the fragment is
left as it is.

Notifications already in your bell keep the wording they were written with — nothing was rewritten
or lost.
