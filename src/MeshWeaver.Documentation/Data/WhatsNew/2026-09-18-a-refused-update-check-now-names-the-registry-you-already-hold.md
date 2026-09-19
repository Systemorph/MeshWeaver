---
Name: A refused update check now names the registry you already hold a key for
Category: Fix
Description: An installation that pulls its images from a container registry validated by another portal has to say which portal that is, or update checks refuse. The refusal explained the rule and named every host except the one the operator would have written — which was sitting in their own configuration all along. It names it now, and still refuses until the pairing is declared.
Icon: LockClosed
Order: -20260918
---

# A refused update check now names the registry you already hold a key for

Some installations pull their platform images from a container registry that does not itself decide
who may pull. It forwards the caller's key to a *portal* and grants the pull when that portal says
the key is good — which is what lets one key serve both the plugin catalogue and the image pull, and
why the registry and the portal are deliberately two different addresses.

The updater will present that key, but only where the installation has **said** the two belong
together. That rule is a safety property, not a formality: the key is a credential, and "present it
to whatever host the image setting happens to name" is exactly the mistake the rule exists to
prevent. So an installation that has not declared the pairing gets a refusal, every check, and
nothing is sent.

**The refusal explained all of that and then stopped one word short.** It said to declare the portal
that validates the key. It did not say *which* portal — even though the answer was in the
installation's own plugin-catalogue configuration, and in the common case there is exactly one
candidate there. An operator reading it had to go and find a value the system could have handed
them.

**It now names them.** The refusal lists the plugin registries this installation is configured for.
Where there is exactly one, it says so and says that this is the host to declare. Where there are
several, it lists them and leaves the choice, because choosing for you is the very thing that would
be unsafe. Where there are none, it says that instead — "declare the pairing" is the wrong
instruction for an installation that holds no key at all, and the first step there is to configure
the catalogue.

And where a pairing *was* declared but names a host with no registry configured on it, the refusal
now shows both sides at once: what was declared, beside what is actually configured. Those two being
one character apart, and those two being genuinely unrelated, used to read identically.

**Nothing new is presented anywhere.** Naming a host is not the same as trusting it, and the checks
are unchanged: the pairing still has to be declared, the key still only ever goes where two separate
statements say it may, and an installation with one registry configured is refused exactly as before
until it declares one. A registry whose configured address carries a password inside it is still
never repeated back — not its host, and certainly not the password — because that text travels to
the log store and onto the update-status record.
