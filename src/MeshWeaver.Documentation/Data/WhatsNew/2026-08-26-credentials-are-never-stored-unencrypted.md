---
Name: A credential is never stored unencrypted, even by accident
Category: Fix
Description: A deployment with no encryption key configured now refuses to store API keys, tokens and credentials rather than quietly saving them in plain text.
Icon: ShieldLock
Order: -20260826
---

# A credential is never stored unencrypted, even by accident

Credentials you give the platform — a model provider's API key, a GitHub token, the credential that
connects an installation to a plugin registry, a connected mailbox's sign-in token — are encrypted
before they are saved. That encryption needs one setting: a master key for the deployment.

Until now, a deployment that did not have one carried on regardless. It saved the credential
*unencrypted* instead, and said nothing: no error, no warning, and the credential still worked
perfectly, so there was nothing to notice. The only visible difference was that anyone who could
read the page could read the key.

That is no longer possible. Without a master key the platform now **refuses** to store the
credential at all, and the refusal names the setting to configure and the two ways to supply a
credential without storing a literal copy of it. Reading stays unchanged, so a deployment that
already holds credentials saved the old way keeps working after this update — it simply cannot add
new ones until it is configured.

The one place this had to stay quiet is the boot-time step that copies a key from a deployment's own
configuration onto its provider: it reports the same refusal in its log, per provider, instead of
stopping the rest of the startup.

**If you administer a deployment where this was happening, changing the code does not un-leak
anything.** Any credential already stored in the clear is still readable — including in the page's
version history — and needs to be rotated at its source. This update stops new ones from joining
them.
