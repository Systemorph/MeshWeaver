---
Name: About now tells you whether the build you are on is current
Category: Feature
Description: The About page already named the exact build a portal runs; it now also says whether a newer one exists — for every user, not only platform admins.
Icon: ArrowSync
Order: -20260809
---

# About now tells you whether the build you are on is current

Settings → About has always answered *what is this portal running*: the version, the
git commit it was built from (linked to the commit on GitHub), the runtime, and the
installed plugins. What it could not answer was the question people actually ask after
a deployment — *is that the latest one?*

That answer existed, but only for platform admins, on the Updates tab. Everyone else
could read a version number and had no way to tell whether it was three weeks old.

About now carries one more line:

- ✅ **Up to date** — no newer build has been detected.
- ⬆️ **Update available** — followed by the version that supersedes the running one.

## Nothing new is being collected

The comparison was already being made. The self-update poller checks the registry a
few times a day and records the newest build it finds; the Updates tab has shown that
to admins all along. The About line is the same two numbers — the one you are on, and
the newest one known — reduced to a verdict.

## What is deliberately not shown

Only the verdict, and the newer version when there is one. The update *strategy*
(continuous, stable or off), the CI-verified-builds setting, and the poller's own
bookkeeping stay on the admin tab, because those are decisions about the deployment
rather than facts about it. Nothing else moved out from behind the admin gate.

## When it says nothing at all

If the line is absent, that is deliberate. An installation with no update checking
configured — or one where automatic checks have been switched off — has no evidence
either way, and a reassuring "up to date" on the strength of a check that never ran
would be worse than silence.
