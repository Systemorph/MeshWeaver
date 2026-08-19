---
Name: Privileged work hands your identity back
Category: Fix
Description: Sign-in, page routing, installs and invitations briefly run with system rights; those rights no longer stay behind on the thread that asked for them.
Icon: ShieldKeyhole
Order: -20260818
---

# Privileged work hands your identity back

Some steps genuinely have to run as the system rather than as you. Checking an API token is the
clearest example: it is what turns a token into an identity, so at the moment it runs there is no
identity yet to run as. The same is true of resolving which page a URL points at, installing a
package into a shared catalogue, or writing an invitation into somebody else's space.

Those steps were switching to the system account correctly — and then leaving it switched on. The
switch was released when the privileged work *finished*, which happens on a different thread from
the one that started it, so the thread that asked was left holding system rights for everything it
did next. For a signed-in request that meant the rest of the request ran with more rights than the
person had; in one path a token that carried no e-mail address could even be adopted as the
caller's own identity for the remainder of the request.

The same mismatch worked in the other direction too: the release wrote the *asking* user's identity
onto whichever background thread happened to finish the work, handing a shared worker a principal
that had nothing to do with what it was processing.

Both halves are closed. A privileged step now covers exactly the work it was opened for and returns
the calling thread to the identity it had, and a release can no longer take effect anywhere the
switch was never applied. Nothing about who may do what has changed — token validation, routing,
installs and invitations still run with the rights they need — only how far those rights travel.
