---
Name: Saving a node always gets an answer
Category: Fix
Description: A create-or-update could finish its work but never report back, leaving the caller waiting.
Icon: Sparkle
Order: -20260810
---

# Saving a node always gets an answer

When something saved a node that did not exist yet — a repository sync, an import, or the
compile status the platform records for you — the save occasionally completed but never
reported back. Whatever was waiting for the confirmation simply waited, and the work looked
stuck even though it had already succeeded.

The reply was being discarded because the reply could arrive fractionally before the sender
was ready to listen for it, and a busy machine made that far more likely. The sender now
starts listening before it asks, so the confirmation can never be missed.
