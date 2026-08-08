---
Name: Cancelled and failed runs now count their tokens
Category: What's New
Description: Tokens consumed before a round was cancelled or hit an error are now recorded against the model that actually ran, so usage and cost reporting reflect real spend.
Icon: Sparkle
---

# Cancelled and failed runs now count their tokens

When you stopped a run mid-flight, or a run ended in an error, the tokens the
model had already consumed could vanish from usage and cost reporting — and
whatever was recorded was attributed to the model you asked for, not the model
that actually ran (a delegated sub-run could even land under "(unknown)").

Cancelled and errored rounds now record their consumed tokens the same way
completed rounds do — keyed to the model that actually served the round, which
is the model the provider reports when it routes your request elsewhere. The
message shows that same model. An errored round no longer loses its usage
record when writing the error message itself fails.

Where a provider reports usage only at the very end of a stream and the round
stops before that report arrives, there is still no count to record — nothing
is estimated in its place.
