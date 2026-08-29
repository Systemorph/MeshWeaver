---
Name: A platform release wakes the fleet once — and only after Plugins is published
Category: Fix
Description: The first push-driven release wave dispatched to every repo before the Plugins publication was sealed, with a payload the receivers could not use, and the Plugins content it needed to bake could not even be checked out. One fan-out now fires after the Plugins bake, carrying the released image, and the bake reads Plugins with a credential that can.
Icon: ArrowSync
Order: -20260830
---

# A platform release wakes the fleet once — and only after Plugins is published

When the platform publishes an image, every node repository — Crm, Reinsurance, SocialMedia,
Education, Manufacturing — is meant to rebuild against it automatically, and Plugins is meant to be
built *with* the platform so those rebuilds find its publication already sealed. The first release
that tried to do this on its own did three things wrong at once, and the delivery verdict correctly
refused to call it delivered.

- **It could not read Plugins.** The platform's release run bakes Plugins' modules, but the job
  checked the private Plugins repository out with the run's own token, which cannot see it. The
  bake died on "repository not found" before packing anything.
- **It woke the fleet twice, and too early.** Two fan-out jobs had been built side by side; the
  one that fired ran straight after the images were tagged, before the Plugins bake, so every
  repository it woke would have looked for a Plugins publication that did not exist yet.
- **It said which version, not which image.** The receivers rebuild against an image reference
  and digest; the event carried a version string, which one repository refuses outright and
  another cannot bake from.

Now the platform's bake reads Plugins through the release App's credential — asserted red before
the checkout, never silently replaced by the token that cannot — and there is one fan-out. It runs
only once the Plugins publication for the released identity is sealed, and its payload names the
image, its digest and the platform commit, which is what every subscribed repository already reads.
A guard pins that shape so the second job cannot grow back.
