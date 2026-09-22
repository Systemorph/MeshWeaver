---
Name: The update settings you choose are the ones that take effect
Category: Fix
Description: On the Updates settings tab, the update-strategy dropdown wrote its value where nothing read it, and the "only CI-verified builds" checkbox could be unticked but never stayed unticked. Both changes now reach the install — and both looked, until now, as though they already had.
Icon: Bug
Order: -20260918
---

# The update settings you choose are the ones that take effect

An installation decides for itself when to take a new version of the platform, and an admin sets
that on **Settings → Updates**: a strategy (follow continuous builds, take clean releases only, or
never update automatically), a version pattern, and a checkbox for accepting only builds that
passed CI.

**Two of those controls did not change anything, and neither said so.**

## The strategy dropdown wrote to the wrong place

The editor on that tab reads and writes the settings record field by field, under the names the
record stores them by. The strategy field had been renamed internally — for a good reason, so that
an install with *no* strategy recorded would fail safe and stop updating rather than take anything
on offer — and the editor kept writing under the old name.

So picking a strategy wrote the value under a name the settings record does not use — and the record
then discarded it entirely on its way to being saved. **The dropdown went on showing the choice, so
it looked applied, while nothing was stored and the install went on following whatever it followed
before.** Leaving the page and coming back showed the field empty again, with nothing to say why.

That is worse than it sounds for one particular install: the one whose strategy had already gone
missing. Such an install fails safe — it stops checking for updates at all — and this tab is the
only place to turn updates back on. Turning them back on did not work, and because nothing checks
for updates while they are off, nothing ever contradicted the tab. The install stayed on the version
it had.

The dropdown now writes where the setting is actually read. There was also a **second**
strategy dropdown on the tab, an unlabelled one that came from an internal convenience value; it is
gone.

## "Only update to CI-verified (green) builds" could not be unticked

Unticking that box stored the value, but the settings are saved in a form that leaves out anything
sitting at its built-in value — and "off" *is* the built-in value for a checkbox. So the setting was
dropped on the way out and came back on as the box's own default of "on" the next time the record
was read.

The box now records both states, so unticking it sticks.

## What to do

Nothing changes on its own, and no setting you made is altered by this release. If you set an update
strategy on this tab before and the install did not follow it, **set it again** — it will take
effect this time. The same goes for the CI-verified checkbox if you meant to untick it.
