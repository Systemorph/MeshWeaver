---
Name: A partition closed to the API is now closed on every path
Category: Fix
Description: Marking a partition as not reachable through the API held for reads a token asked for by name, but not for requests routed to the node that owns the data — most tokens carry no role claims, and that was the condition deciding whether the check ran at all. The setting now applies on every path.
Icon: ShieldKeyhole
Order: -20260902
---

# A partition closed to the API is now closed on every path

An administrator can declare that a partition is readable in a browser but **not reachable through
the API** — the setting that says "people may read this page; automated clients may not". It is the
one lever that takes API reach away from tokens that already exist, so it has to hold everywhere.

It did not. It held for a read that named the page directly, and it was skipped for a request routed
to the node that owns the data — the shape most operations actually take.

## Why it was skipped

Every request carries the identity of whoever made it, including the fact that it arrived through an
API token rather than a browser. That identity was only re-established on the receiving side when it
carried **role claims** — a list of role names attached by the sign-in provider.

Most sign-in providers attach no role names at all. So for the ordinary token the list was empty, the
identity was not re-established, the receiving node never learned the request came from a token, and
the check that would have closed the door never ran. Nothing failed and nothing was logged: the
request was simply evaluated as though it had come from a signed-in browser session.

The condition was left over from an older design in which those role names decided what a person
could read. That design is gone — role names have granted nothing since the access model was
corrected — but the condition outlived the reason for it, and it was never the right condition
anyway: the two facts actually needed are *this came from an API token* and *this came from a hub*,
neither of which has anything to do with roles.

## What changed

The caller's identity is now re-established whenever the request carries one, so the receiving node
sees the same facts the direct read path already saw. Concretely:

- **`api: false` applies on every path.** A partition closed to the API is closed to a token whether
  the token reads the page by name or drives an operation against it.
- **No re-issuing anything.** Existing tokens are affected immediately; the decision is made from the
  live policy on the page being read, never from the token.
- **Nothing became less reachable for people.** A public page stays readable in a browser, and a
  token whose owner holds a real grant on the target keeps working exactly as before — this closes a
  door that was meant to be closed, and opens none.
