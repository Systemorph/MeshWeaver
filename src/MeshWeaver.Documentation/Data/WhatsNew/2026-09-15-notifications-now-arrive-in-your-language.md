---
Name: Notifications now arrive in your language
Category: Fix
Description: Every row in the notification bell was written in English, whatever language you read the portal in — an update reminder, a failed import, a share someone sent you. Notifications now carry what they mean rather than a finished English sentence, so the bell renders each one in the language of whoever opens it, and the matching email arrives in the recipient's own language.
Icon: Alert
Order: -20260915
---

# Notifications now arrive in your language

The portal renders its buttons, menus and settings in your language — but the **notification bell**
did not. Every row in it was English: *"Update available: …"*, *"Startup import failed: …"*,
*"You've been given access to …"*. So did the emails those notifications send.

## Why it happened, and why it was not simply an overlooked string

A notification is written by the portal working in the background — a plugin checking for updates, a
module scan, an import running at startup, a page someone was granted access to. **Nobody is looking
at the screen at that moment**, so there is no language to write it in; the portal picked its own
default, English, and stored that finished sentence on the notification.

That is also why a handful of these notifications *looked* translated and still were not: the German
text existed, it was simply chosen at the moment of writing, when the only answer available was
"English".

## What changed

A notification now stores **what it means** — which message it is, and the details that go in it
(*which* module, *which* partition, *how many* errors) — instead of a finished sentence. The bell
puts the sentence together when you open it, in **your** language, with those details filled in.
This covers the reminders you actually see:

- plugin and module updates — available, held back, or needing a global administrator
- a module discovered, added, refused or no longer offered by its repository
- a plugin registry that could not be reached during startup
- an import that failed at startup, and a page type that failed to compile or is serving a
  placeholder
- someone giving you access to a page or space

**Emails follow the recipient.** A notification email has exactly one reader, so it is written in
*that* person's language — subject, body, the button and the footer note — rather than the server's.

## What stays in English

Text the portal did not write itself: an error message from a compiler, an exception quoted from
another system, the raw startup log lines that a report carries. Those are shown exactly as they
arrived, in every language, which is more honest than a half-translation. Where such a fragment sits
inside a sentence the portal *did* write, the sentence around it is translated and the fragment is
left as it is.

Older notifications already in your bell keep the wording they were written with — nothing was
rewritten or lost. New ones arrive in your language from now on.
