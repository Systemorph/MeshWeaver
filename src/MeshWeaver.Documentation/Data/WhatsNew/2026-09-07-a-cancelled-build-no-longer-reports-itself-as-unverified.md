---
Name: A cancelled build no longer reports itself as unverified
Category: Fix
Description: When a build was cancelled before producing anything, the check that verifies builds reported a failure saying nothing had been verified — which was true but misleading, because there was nothing to verify. It now says so plainly instead.
Icon: CheckmarkCircle
Order: -20260907
---

# A cancelled build no longer reports itself as unverified

Builds here are cancelled routinely — a newer one supersedes the one waiting. The check that
verifies a build against each live installation treated that as a failure, reporting that nothing had
been verified and pointing readers at a step that had never run. True, but the wrong conclusion: a
cancelled build produced nothing, so there was nothing to verify.

It now distinguishes the two. A cancelled build reports "no candidate" and passes. A build that
succeeded but went unchecked still fails loudly — that one is a real gap, and it is the reason the
check exists.
