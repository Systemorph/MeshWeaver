---
Name: A held installation token refreshes on every expiry
Category: Fix
Description: The Store's package feed on a portal that reads GitHub as a GitHub App no longer freezes two hours after boot. The installation-token cache refreshed exactly once per held observable and then served the expired token forever, which GitHub reported as "Bad credentials" on every private source; it now refreshes at every subscription that finds the token within five minutes of expiry.
Icon: Checkmark
Order: -20260906
---

# A held installation token refreshes on every expiry

A portal that polls its package sources as a **GitHub App installation** mints a token that lives
one hour. The Store's git poll loop holds one token observable for the life of its feed and
subscribes to it once per pass, every five minutes. The cache behind that observable refreshed the
token the first time it neared expiry — and never again: the refresh guard compared against the
promise captured when the observable was built, found it already replaced, and handed the expired
token back on every later pass. Two token lifetimes after boot, every private source read as
`401 Bad credentials`, the catalog froze on its last good snapshot, and the log said the credential
had been rejected — which sent the investigation to GitHub and to the deployment's configuration
rather than to the cache (Systemorph/Memex#165).

The observable is now deferred: each subscription reads the current cached token, replays it while
it is fresh, and mints a replacement — shared by concurrent subscribers — when it is within five
minutes of expiry. A regression test drives the held observable through three expiries with an
injected clock and asserts a new token at each, and a fresh one replayed in between.
