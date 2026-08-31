---
Name: A brief registry hiccup no longer freezes your installed packages until the next restart
Category: Fix
Description: When the plugin registry answered "temporarily unavailable" while an instance was starting, that instance stopped reconciling its installed packages for as long as it stayed up — no updates, no grant changes, and no sign anything was wrong. A momentary outage is now retried during startup instead of being mistaken for a refusal.
Icon: Sparkle
Order: -20260831
---

# A brief registry hiccup no longer freezes your installed packages until the next restart

An instance that installs from a registry checks in with it once at startup, compares what it has
against what the registry now serves, and lands anything newer. There is deliberately no background
poll behind that check — the promise is simply "you pick up changes the next time you start" — which
also means the startup attempt is the only chance an instance gets.

That attempt already retried a network stumble. What it did not retry was the registry answering
**503 — temporarily unavailable, retry shortly**: the code could not tell that apart from the
registry answering **403 — you are not allowed**, because every unsuccessful answer arrived as the
same kind of error, with the status buried in the message text. Declining to re-ask a refusal is
right; a registry that has refused your key will refuse it again a second later. Applying that rule
to a momentary outage was not — it turned a hiccup lasting seconds into an instance that never
reconciled again.

The consequence was quiet, which is what made it worth fixing. The instance kept running and serving
pages normally; it simply stopped noticing that packages had new versions or that grants had changed,
until somebody restarted it or opened the catalog page by hand.

The status the registry sends now travels as data rather than as prose, so startup can tell "come
back shortly" apart from "no". A temporary answer — 503, 429, or a gateway error — is retried within
the existing startup budget; a definite one still fails immediately and says so in the log, because
waiting on it would only delay the message naming what is actually wrong.

This matters more than it did a day ago: installing from the cloud registry and registering without a
key both shipped on 2026-08-30, which puts the registry on the startup path of every new local
install.
