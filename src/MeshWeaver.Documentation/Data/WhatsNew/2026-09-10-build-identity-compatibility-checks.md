---
Name: Compatibility checks use a build identity
Category: Feature
Description: Trusted builds can read a portal's release inputs and record compatibility results without receiving an administrator credential or changing its update policy.
Icon: ShieldCheckmark
Order: -20260910
---

# Compatibility checks use a build identity

A trusted build can use its short-lived GitHub identity to read a portal's release target and
installed module inventory, run compatibility checks outside production, and record the result.
The portal requires an explicit grant for these operations. The build receives no general access
to user data or administrator settings.

Recording a result preserves the chosen update policy, including disabled updates. A successful
response confirms that the result is stored on the portal.
