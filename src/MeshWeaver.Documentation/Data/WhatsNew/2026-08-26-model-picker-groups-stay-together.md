---
Name: A pinned model no longer splits its provider's group in the model picker
Category: Fix
Description: A promoted model could tear its provider's block in two, making the same provider appear under two separate headers in the model picker.
Icon: List
Order: -20260826
---

# A pinned model no longer splits its provider's group in the model picker

The model picker groups models by provider. Promoting one model within a provider — so it becomes
the deployment's default — could push that single model out of its provider's run in the list,
making the provider render as two separate headers with the promoted model stranded between them.
Provider is now the primary grouping key, so every provider's models always render as one
contiguous block; a promotion still moves the model to the top of its own group, exactly as
intended, without tearing the group apart.
