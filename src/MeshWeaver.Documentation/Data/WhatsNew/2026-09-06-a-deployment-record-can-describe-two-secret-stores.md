---
Name: A deployment record can finally describe everything its portal runs with
Category: Fix
Description: A portal that read two Key Vault secret stores could only be half described, so regenerating its configuration deleted the other half. It can now hold both — plus the settings someone had only ever typed into the cluster.
Icon: ShieldCheckmark
Order: -20260906
---

# A deployment record can finally describe everything its portal runs with

Every portal we operate has a **record** that says what it is: its host, its database, how much
memory it gets, which secrets it reads. The idea is simple — change the record, regenerate the
configuration, deploy. The record is the source of truth and nothing is kept in step by hand.

That only works if the record can describe the portal. Ours could not, and the gap was the dangerous
kind: regenerating a portal's configuration from its own record **deleted settings that were keeping
it running**, silently, because the record had nowhere to put them.

## What was wrong

A portal reads its passwords and API keys out of a secure vault. The record had room for **one**
such connection. Our own portal uses **two** — one holding its database connection, its GitHub key
and its AI provider keys, and a second, newly added, holding the credential it uses to fetch plugin
updates.

With room for only one, the record described one and the second existed only in a separate file.
Regenerating from the record would have deleted the plugin credential added that same morning to fix
a broken update feed, detached thirteen other keys from the running portal, and pointed it at a
vault entry that does not exist — which stops new instances from starting at all. Measured against
the deployed configuration, forty-one settings would have been lost.

There was a second, quieter problem. The record refused to let one vault entry be read under two
names — and that is exactly what the plugin-update fix needed, because two parts of the portal look
for the same credential under different names. The same refusal had already been quietly losing one
of the AI provider keys on the public portal.

## What changes

A record can now describe **as many secret stores as the portal actually reads**, and one vault
entry may be read under as many names as the portal looks for. The reverse is now checked instead,
which is the rule that matters: a single setting may only come from one place. Two sources for one
setting means whichever the system happens to read last wins, and that is how a portal once crashed
on startup with half its email configuration.

A record can also now write down two things that only ever existed on the running machine:

- **Settings typed straight into the cluster.** These outrank everything a file can say, and a
  deploy does not remove them. Writing them down does not create or delete them; it stops the record
  from disagreeing with reality at every read, so that anything left unexplained is a genuine
  surprise. Each one now says what it overrides, whether the two agree, why it is still there, and
  what will retire it.
- **Extra programs running alongside the portal.** Our own portal has been running two
  language sandboxes for weeks that no file mentioned.

Along the way, this made a few things visible that nobody had written down: a setting the public
portal reads as *off* while every file says *on*; three settings that would simply vanish if someone
"tidied up" the cluster, because nothing else supplies them; and a vault entry two portals are
configured to read that has never been created — which will stall their next deploy until it is.

Credentials themselves are still never recorded. A setting that carries one says so and leaves the
value out, and a record that tries to keep both is refused rather than quietly trimmed.
