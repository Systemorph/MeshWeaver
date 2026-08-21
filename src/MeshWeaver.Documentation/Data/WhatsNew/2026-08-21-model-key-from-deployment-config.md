---
Name: The model you pick is the model that runs
Category: Fix
Description: Models whose API key comes from the deployment configuration are no longer treated as unusable and silently swapped for another model.
Icon: Sparkle
Order: -20260821
---

# The model you pick is the model that runs

Picking a specific model in the chat composer could quietly get you a different one. If a
deployment kept a provider's API key in its configuration rather than on the provider's entry
under Settings ▸ Language Models, the platform read that provider as having no key at all — even
though every round it served worked perfectly. Any model of that provider was then treated as a
stale selection and replaced with the deployment's default, without a word in the chat.

Credential lookup now consults the deployment's own configuration for a provider as well as its
stored entry, so a model that can actually be served is reported as usable and your explicit
choice is kept. A model with no key anywhere is still reported as unusable, so the platform still
steers you away from a selection that would only produce a provider error.
