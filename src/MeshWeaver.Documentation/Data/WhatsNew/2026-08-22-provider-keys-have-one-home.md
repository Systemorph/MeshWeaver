---
Name: A provider key has one home
Category: Improvement
Description: An API key set in a deployment's configuration now lands on the provider's entry under Settings ▸ Language Models — encrypted — instead of living somewhere only the servers could see.
Icon: Key
Order: -20260822
---

# A provider key has one home

A model provider's API key could live in two places at once: on the provider's entry in the
platform, and in the deployment's own configuration. The two never met. A key added to the
deployment after the provider entry already existed reached the servers and never reached the
entry — so the platform showed a provider with no key while every round it served worked, and
anyone looking for the key had no way to tell which of the two was actually in use.

The provider's entry is now the one place a key lives. A key configured in the deployment is
copied onto it at startup — encrypted at rest, and only when the entry has no key of its own, so
a key you set or rotate here is never overwritten. Nothing reads the deployment's copy afterwards:
one place to look, one place to rotate, and no silent disagreement between them.

If a deployment has no encryption master key configured, the copy is refused outright rather than
storing a credential unencrypted, and says so loudly in the logs — set the master key and restart.
