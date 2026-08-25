---
Name: Models added to a deployment actually appear
Category: Fix
Description: A provider created before a model was configured could never gain that model — and the import that skipped the work recorded itself as a success, so no later start-up even tried again. Both are fixed.
Icon: Sparkle
Order: -20260825
---

# Models added to a deployment actually appear

A deployment adds a dozen models to a provider's configuration, restarts, and **not one of them
appears**. No error, no warning, nothing in the log. The provider keeps whatever model list it was
born with, and neither a restart nor recycling the provider changes anything.

Two separate things had to go right for that, and both are now fixed.

A provider node is deliberately "create once, then the administrator owns it" — that is what keeps a
key or an endpoint you typed in from being overwritten on the next deploy. But the claim was written
to cover the provider *and everything under it*, and the models are underneath. So a model added
after the provider node already existed was refused on every start-up, forever. The claim now covers
the provider node itself and leaves its models to follow the deployment's configuration, which is
what makes the model list manageable at all.

The second half is the one that made it invisible. The import recorded "0 nodes imported" as a
**success** — and a successful import is the marker every later start-up reads to decide it has
nothing to do. Work that was declined is now counted, and an import that could not create something
the deployment declares says so by name, is recorded as a warning rather than a success, and is
retried on the next start instead of being quietly written off.
