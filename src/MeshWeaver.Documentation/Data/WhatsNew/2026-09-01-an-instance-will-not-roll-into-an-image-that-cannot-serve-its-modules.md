---
Name: An instance will not roll into an image that cannot serve its modules
Category: Fix
Description: The self-update decision now consults the combo verification, so a build that a check has shown cannot run this deployment's modules is refused — and the refusal names which modules would break.
Icon: ShieldCheckmark
Order: -20260901
---

# An instance will not roll into an image that cannot serve its modules

A build being newer never meant it could run what a deployment has already installed. A platform
change can invalidate the compiled content a module ships, and the update would still be offered,
taken, and applied — leaving a portal that restarts into an image its own modules cannot bind
against. One portal was stuck in exactly that state: it could not move forward, because the new
platform could not serve its old modules, and it could not refresh its modules, because they needed
the new platform. It stayed up only because both halves were equally out of date.

The check that answers that question already existed. It runs a deployment's real module set inside
a candidate image, compiles, renders and tests each one, and reports one of three answers: **green**,
**red with every failing module named**, or **could not be determined**. What was missing was anyone
asking it before rolling — so the answer was recorded nowhere and read by nothing.

Now the update decision consults it:

- **Green** — the update proceeds, and any caveats the check raised are carried with it.
- **Red** — the update is **refused**. The Updates settings tab, the About page and the header build
  chip all show it as held rather than as available, and the reason names each module that would
  break, so the fix is obvious instead of a mystery.
- **Could not be determined** — deliberately neither. Treating it as a pass is what caused the
  problem above; treating it as a failure would stop every deployment updating the moment a check
  could not run. The update proceeds on the strength of the other checks, and the record says
  plainly that it was taken unverified, and why.

Nothing has to be un-stuck by hand: a refusal is re-decided on every check, so re-verifying a build
clears it. And a deployment that has never had this check run is not blocked by it — it simply reads
"no verification has been recorded", with the command that produces one.
