---
Name: A token that was never issued no longer reads as a rejected one
Category: Fix
Description: When the portal could not obtain its GitHub credential at all, the failure surfaced one layer later as "Bad credentials" — the message for a credential GitHub turned down. The two are now different, named events, and the one that says "never issued" says where it stopped.
Icon: ShieldKeyhole
Order: -20260918
---

# A token that was never issued no longer reads as a rejected one

The portal reads private GitHub repositories — the plugin catalog's sources, a Space's sync — with a
machine credential it mints for itself, roughly once an hour, from the platform's GitHub App. Two
things can go wrong with that, and they need opposite responses:

- **The credential was obtained and GitHub turned it down.** Look at what the installation is
  allowed to see.
- **The credential was never obtained at all.** Look at the App itself — its key, or whether it is
  still installed.

Until now you could not tell which one you were looking at.

## Why the second one wore the first one's clothes

A reader that cannot mint a token does not necessarily stop. The Store's package feed, for one,
deliberately carries on without one: plenty of sources are public, and a catalog serving what it can
is better than a catalog serving nothing. So it fetched anonymously — and the *next* thing that
happened was GitHub refusing the private repositories, which it reports as

> `AuthorizationException: Bad credentials`

That sentence is about a credential that was presented and judged. Read against a credential that
was never issued, it is an accurate description of the wrong event: it sends the reader to the
installation's repository permissions, which are fine, and away from the private key or the
installation, which are not. Every failing pass said it again, five minutes apart, for four days.

The mint's own failure had nothing to distinguish it — it was the same plain error type any number
of unrelated things throw, so the only thing a reader could do with it was print its message and
hope somebody read the whole chain.

## What changed

A mint that fails now raises **its own kind of failure**, and that failure says how far it got:

- the App identity is **not configured** at all — a deployment choice on most instances, a
  misconfiguration on one that polls private repositories;
- the App's key could not **sign** — nothing ever reached GitHub, so no credential was judged;
- the **installation** could not be found — the App is not installed where it is expected to be;
- the **exchange** was refused — this is the nearest thing to "revoked", and it is *still* not the
  same event as a repository read being refused;
- the response carried **no token**, or the request never reached a **verdict** at all.

Anything that degrades to an anonymous read can now say which of those it is degrading over, without
anyone having to match on the wording of a message. And a report that used to assert "could not mint
a token" for whatever had gone wrong — the framework-release broadcast did exactly that — now says
which of the two it actually saw.

Nothing retries differently, nothing waits longer, and no credential is handled anywhere new. The
failure is simply no longer able to arrive wearing another failure's name.
