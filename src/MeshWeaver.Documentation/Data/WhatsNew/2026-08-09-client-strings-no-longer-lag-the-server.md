---
Name: Client text no longer lags behind the server
Category: What's New
Description: Five error messages that showed up as raw codes in the React client now read as proper sentences, and the check that keeps client text in step with the server can no longer be skipped.
Icon: Globe
---

# Client text no longer lags behind the server

Every user-visible string is written once on the server, and the JavaScript clients carry a copy so
they can show text without waiting for a round trip. A check compares the two and fails if they ever
disagree — but it only ran when someone edited the client, so a message added on the server alone
slipped past it.

That is what happened to five of them. Anyone hitting a rate-limited language model, a model whose
provider returned an error, or a temporary problem checking their account saw a bare code like
`chat.modelRateLimited` where a sentence should have been. All five now read properly, in English
and in German.

The check now also runs when the server's text changes, so the copies are compared whenever either
side moves. The same gap applied to the checks that keep the clients' control set and wire format in
step with the server, and those are covered now too.
