---
Name: Pending deliveries no longer start duplicate builds
Category: Fix
Description: The delivery reconciler now recognizes work waiting behind GitHub's concurrency queue, so it does not start a second build for the same commit.
Icon: Sparkle
Order: -20260910
---

# Pending deliveries no longer start duplicate builds

The deployment reconciler now recognizes deliveries waiting behind GitHub's concurrency queue as
live work. An hourly reconciliation can no longer start a second image build for the same commit
while the original delivery is pending.
