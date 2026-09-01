---
Name: An API token no longer works from a copy of your old permissions
Category: Fix
Description: API tokens carried a copy of your roles from the moment they were created. That copy could not see access granted later — and, worse, could not lose access taken away later. Tokens now read your access live, so a new grant works immediately and a withdrawal takes effect immediately.
Icon: ShieldKeyhole
Order: -20260901
---

# An API token no longer works from a copy of your old permissions

An API token — the `mw_…` key that connects Claude, an MCP client or any script to your mesh — signs
in as you. It should see what you see.

Until now it carried something extra: a small copy of your roles, taken at the instant the token was
created and never updated. That copy was consulted when deciding whether a token was allowed to use
the API at all, and a copy of an access fact goes stale in both directions.

**It could not see access granted later.** If someone shared a space with you after your token was
made, the token did not learn about it. The page opened perfectly in your browser and the same read
through the token was refused — the confusing shape where "it works on screen but not in the tool".
Re-making the token was the usual advice, and it often did not help either: most sign-in providers
attach no roles at all, so the copy was empty the first time and empty again on every re-make.

**And it could not lose access taken away later.** This is the more serious half. A token created
while you held a wide role kept the door open indefinitely, even after an administrator marked a
partition as *not reachable through the API*. Closing that door closed it for new tokens and left
every existing one untouched — a withdrawal that silently did not apply.

## What changed

Tokens no longer carry authority. Every request now resolves your access from the live records on
the page being read, exactly the way your browser session does:

- **A grant works the moment it is made.** No re-issuing a token, no signing out, nothing to wait
  for.
- **A withdrawal works the moment it is made.** Marking a partition as not reachable through the API
  now applies to tokens that already exist, which is the only version of that setting worth having.
- **Public pages are readable through the API.** Documentation, the agent catalog and installed
  package content are published for everyone to read; a token can now read them without needing a
  role of its own — while a partition explicitly closed to the API stays closed.

Nothing became more visible. A token still sees exactly what its owner sees and never more; what
changed is only *when* it finds out.
