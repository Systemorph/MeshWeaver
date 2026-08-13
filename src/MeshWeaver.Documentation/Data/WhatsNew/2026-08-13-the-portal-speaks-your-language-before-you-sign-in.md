---
Name: The portal speaks your language before you sign in
Category: Fix
Description: Pages you can reach without an account now follow your browser's language instead of always appearing in English.
Icon: Globe
Order: -20260813
---

# The portal speaks your language before you sign in

The portal renders its buttons, labels and messages in your language, and it worked out which
language that is from your profile. That is fine once you are signed in — and useless before, because
a visitor who has not signed in has no profile. Everyone arriving at a paywall, an invitation link or
a public course page therefore got English, no matter what their browser asked for. It was precisely
the audience the feature was built for that never saw it.

Your browser states its preferred languages on every request, and the portal now reads that
statement. An anonymous visitor whose browser asks for German gets German; one asking for British
English gets English, even on a German-language course. Regional variants fold onto the language
itself, so Swiss German, Austrian German and German all read the same. A language this portal does
not ship falls back to English as before.

Signing in changes nothing about your own choice: a language set in your profile still wins over
whatever your browser says. What changed is the case where nobody had said anything yet — that now
follows the browser rather than defaulting to English, and it does so on the very first render of
the page rather than after a visible switch.
