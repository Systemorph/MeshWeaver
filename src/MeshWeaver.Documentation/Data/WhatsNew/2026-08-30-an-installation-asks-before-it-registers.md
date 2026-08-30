---
Name: An installation asks before it registers
Category: Feature
Description: A new installation registers itself at the plugin registry only after a platform admin has read and accepted the privacy statement and the platform terms in Settings ▸ Instance registration — then it registers, obtains its token and shows the catalogue its plan covers
Icon: Sparkle
Order: -20260830
---

# An installation asks before it registers

A new installation that is configured to register itself at a plugin registry without a key — the Homebrew default — no longer does so silently on first start. It starts, and asks: a platform admin opens **Settings ▸ Instance registration**, reads the privacy statement and the platform terms shown there, accepts both, and registers. The consent is recorded on the installation (who, when, which version of each text); only then does the installation register, obtain its short-lived token and read the package catalogue its plan covers — which the same page shows, together with the instance id and the plan it landed on (free, unless the registry's admins raise it). Withdrawing the consent from the same page stops the installation from registering again. Installations provisioned with a registration key by an operator are unaffected.
