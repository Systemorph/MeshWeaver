---
Name: A platform promotion no longer reds a dependent repository's build
Category: Fix
Description: Promoting the platform rolls the portal that serves the plugin registry, and any repository whose CI read that registry inside the roll window failed on a 503 through no fault of its own change. Those reads now wait out a brief unavailability, while a genuine refusal still fails immediately.
Icon: Sparkle
Order: -20260831
---

# A platform promotion no longer reds a dependent repository's build

Repositories that build modules against the platform ask the registry, during their build, for the
sealed upstream artefacts they compile against. Promoting a new platform version rolls the portal —
and that portal is what serves the registry. For the length of the roll, the registry answers
**503 — temporarily unavailable**, and any build that happened to ask inside that window failed,
reporting an error about a change that had nothing to do with it.

The window is structural rather than incidental: every promotion opens one, six repositories read
that endpoint, and on a day with three promotions it opened three times. The more reliably promotion
works, the more often somebody's build lands inside it. Re-running the build was the only recourse,
which is the habit worth removing — it teaches people to re-run red builds without reading them.

A registry that has not answered yet is now told apart from a registry that has answered *no*. A
temporary condition — 503, 429, a gateway error, or a connection that never landed — is re-asked
with a widening pause, over about three minutes. A definite answer is untouched: a missing
publication, or a key the registry refuses, still fails at once and says exactly what is wrong,
because waiting on one of those would only delay the message naming it.

A registry that is genuinely down still fails the build, and still fails it red — just after it has
been given a fair chance to come back.
