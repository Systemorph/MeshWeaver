---
Name: A missing access gate now refuses instead of waving everyone through
Category: Fix
Description: A portal configured without access control used to serve every partition to every signed-in visitor, with nothing in the logs to say so. It now refuses to start unless the operator says the mesh is meant to be open.
Icon: ShieldError
Order: -20260811.1
---

# A missing access gate now refuses instead of waving everyone through

Access control on a MeshWeaver mesh is switched on by one line of configuration. If that line was
missing, nothing said so. Permission checks did not fail — they answered "allowed", every time, for
every caller and every path. A portal in that state looked completely healthy: pages loaded, the
logs were clean, and every signed-in visitor could read every partition in the mesh.

That is the failure mode this release removes. A gate that was never installed is no longer allowed
to look exactly like a gate that everybody passed.

Two things changed, at the two points where the omission can be caught.

A portal that publishes mesh content over HTTP now checks at **startup** that access control is
actually installed, and refuses to start if it is not — naming the exact line to add rather than
leaving it to be discovered from behaviour. The misconfiguration is static and knowable before the
first visitor arrives, so that is where it is reported.

And the request-time gate no longer skips itself. Where the permission pipeline is installed but has
nothing to evaluate against, it now refuses the request instead of passing it through. The refusal
says that no decision could be reached — deliberately not "you lack permission", which would be a
claim nobody established and would send a correctly-entitled user off to request access they already
have.

Running a mesh without access control is still supported, because some genuinely need it: a
single-user sidecar, an embedded instance, a test fixture. What changed is that it now has to be
said out loud. A host declares itself open with `AllowUnsecuredMesh`, giving a reason, and that
reason is written into the startup log where the next person to read it can tell a deliberate choice
from an accident. The absence of a setting is no longer accepted as a statement of intent.

Existing portals are unaffected — they all configure access control already, and their behaviour is
unchanged.

Alongside this, a content request that fails for an unexpected reason no longer repeats the internal
error text back to the caller. Those messages are written for operators and can name the permission
model, the requesting identity, and the fact that a particular node exists. The detail now goes to
the server log; the caller gets a plain error. A refused file and a missing file continue to answer
identically, so neither response can be used to map which files exist.
