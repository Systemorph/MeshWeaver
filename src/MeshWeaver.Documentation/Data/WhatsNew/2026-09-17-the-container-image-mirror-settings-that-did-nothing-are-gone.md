---
Name: The container image mirror settings that did nothing are gone
Category: Fix
Description: The chart's containerImages settings configured an in-portal image mirror that no portal ever switched on, so setting them produced neither a mirror nor an error. The unused mirror and those settings are removed — installations keep pulling from cr.meshweaver.cloud exactly as before.
Icon: Delete
Order: -20260917
---

# The container image mirror settings that did nothing are gone

The deployment chart offered a `containerImages` block — an upstream registry, a username, an
allowlist of repositories, a cache directory and size — for an installation to serve container
images to the rest of the fleet through its own `/v2` endpoint. The code behind it was built and
tested, but **no portal ever switched it on**. So an operator who filled in the block got a
configuration that looked wired and did nothing: no mirror, and no error saying so. Even the check
that was meant to refuse a half-configured mirror could never run, because it lived inside the part
that was never switched on.

The fleet's images come from `cr.meshweaver.cloud`, which is a separate registry service, not this
code. With that settled, the unused mirror is **removed**, together with the `containerImages`
chart settings and the `ContainerImages__…` configuration keys they rendered.

**What you need to do:** nothing, unless one of your own values files still sets `containerImages`
— then delete that block and any `ContainerImages-Password` Key Vault mapping that went with it.
They had no effect before and have none now. How an installation pulls its platform images is
unchanged: `selfUpdate.registry` and `portal.imagePullSecret`, as described in
[A Container Registry in Memex](/Doc/Architecture/ContainerRegistryInMemex).
