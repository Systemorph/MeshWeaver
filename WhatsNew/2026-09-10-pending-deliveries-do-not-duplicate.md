---
Title: Pending deliveries no longer start duplicate builds
Category: Fix
---

The deployment reconciler now recognizes deliveries waiting behind GitHub's concurrency queue as
live work. An hourly reconciliation can no longer start a second image build for the same commit
while the original delivery is pending.
