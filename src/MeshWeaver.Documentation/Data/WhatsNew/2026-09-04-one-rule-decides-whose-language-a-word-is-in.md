---
Name: One rule decides whose language each word on a page is in
Category: Fix
Description: A German reader of an English lesson met a lone German button label in the middle of an English sentence. The platform now has one settled rule for whose language a word renders in — the owner's — and a second that keeps the application's words out of the middle of an author's sentence.
Icon: Globe
Order: -20260904
---

# One rule decides whose language each word on a page is in

The portal shows its own words in your language and shows what an author wrote in the language they
wrote it in. On a page that mixes the two — an English lesson read by someone whose portal is in
German — that produced a single German verb sitting inside an English sentence. Each piece was
behaving correctly on its own, and the page still read as broken.

There is now one rule for it, and it is about **ownership** rather than position: every word on a
page belongs either to the platform, to a module, or to the author, and whoever owns it decides its
language. The application's words follow you. The author's words stay exactly as written. A word
that belongs to nobody — text typed straight into a view and never translated — is a bug, and it
gets an owner rather than a special case.

The second half of the rule is restraint. Where the application has to place a control *inside*
something an author wrote — the Run button on a code cell, for instance — it now uses a symbol
wherever a symbol says the same thing, and moves the translated words into the tooltip, which is
also what a screen reader announces. Nothing is lost, and no translated word lands in the middle of
somebody else's sentence.

This arrives control by control rather than all at once, and the code cell's Run button is the
first. The obvious alternative — making the buttons follow the *course's* language instead of yours
— was considered and declined: it was tried once before, and it hands an English reader of a German
course a page whose every control is in a language they cannot read.
