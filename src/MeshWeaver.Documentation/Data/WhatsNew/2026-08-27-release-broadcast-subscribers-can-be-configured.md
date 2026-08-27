---
Name: A deployment can finally name who gets told about a new release
Category: Fix
Description: The subscriber list for the platform-release broadcast is now a real deployment setting, and an instance that receives release events but has nobody to tell says so.
Icon: Sparkle
Order: -20260827
---

# A deployment can finally name who gets told about a new release

When a new platform build ships, the instance that receives it is meant to tell every dependent
repository straight away, so each rebuilds against the new version in seconds rather than waiting
for its next scheduled check.

That list of dependents was read from a setting no deployment could actually set — nothing in the
deployment configuration ever produced it. Every broadcast therefore ran against an empty list and
reported the same thing an instance with genuinely nobody to tell reports: nothing sent, nothing
failed. No error, no warning, no dependent ever notified.

The setting is now part of a deployment's configuration, so a list can be given. And the two cases
no longer look alike: an instance that receives release events and has no list now says so plainly,
naming the setting to fill in, while an instance that simply is not the one distributing releases
stays quiet, as it should.
