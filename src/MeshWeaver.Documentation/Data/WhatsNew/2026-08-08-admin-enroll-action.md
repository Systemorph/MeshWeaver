---
Name: Admins can enroll a user in one step
Category: Feature
Description: Giving somebody access to a package is now a single action — it writes the entitlement record and the access grant together, so nobody ends up "purchased but locked out".
Icon: PersonAdd
Order: -20260808
---

# Admins can enroll a user in one step

Granting somebody access to a Store package used to take two writes that had to agree: the
entitlement record that marks them an owner, and the access grant that actually opens the gated
pages. Buying a package did both. Doing it by hand did not — so an admin who added the record saw
the package listed as owned while every lesson inside stayed locked, with nothing on screen
explaining the contradiction.

Enrollment is now one action. A platform admin opens **Enroll** on the package, names the person,
and clicks once; both records are written together, exactly as a purchase writes them. Automation
gets the same door — a scripted enrollment runs the same code path, so an onboarding script and a
checkout can no longer drift apart.

Only platform admins can enroll. Re-enrolling somebody who already has access is safe: it refreshes
their grant in place rather than duplicating anything, and entitlements never expire.
