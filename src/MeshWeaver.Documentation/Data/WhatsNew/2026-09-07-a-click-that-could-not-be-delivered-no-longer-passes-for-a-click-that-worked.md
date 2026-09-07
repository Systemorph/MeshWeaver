---
Name: A click that could not be delivered no longer passes for a click that worked
Category: Fix
Description: A button press whose page had already closed was discarded with a line that read like routine housekeeping, so nothing anywhere recorded that the action never ran. It is now refused out loud — reported back to whoever is still connected, and logged as the lost action it is.
Icon: Bug
Order: -20260907
---

# A click that could not be delivered no longer passes for a click that worked

You press **Install** and immediately move to another page. The press is on its way to the server;
your page closes behind it; the press arrives a fraction of a second later and finds nothing left to
hand it to.

Until now that press was quietly dropped. The page you had left showed nothing, the page you had
moved to showed nothing, and the only trace was one server line that looked exactly like the routine
housekeeping the server writes whenever a page closes. Anything that later went looking for the
result — you, a colleague, an automated check — saw an absence indistinguishable from *you never
clicked at all*. In one measured case that cost a five-minute wait for a package that was never going
to appear.

**What changed:** an action that cannot be delivered is now *refused*, not discarded.

- If anything of yours is still connected, you get the standard error message: your last action did
  not run, nothing was changed, please try again — in English or German, in your language, naming
  what you clicked.
- Whoever is running the portal gets one clearly-worded error naming the action, the place and the
  page it came from, instead of a housekeeping note.

Ordinary page housekeeping is untouched and stays as quiet as it was — the change applies only to
things a person actually did: a click, a dialog you closed, a field you left.

**What it does not do:** it does not make the press happen after the fact. Once the page is gone the
work it would have started has nowhere to run, and pretending otherwise would be worse than saying
so. What you get instead is the truth, quickly, on whatever is still in front of you.
