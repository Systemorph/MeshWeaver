---
Name: A broken agent tool no longer blocks chat
Category: Fix
Description: When one of an agent's optional tool plugins cannot start, the agent now runs without that tool instead of failing every conversation with an error.
Icon: Sparkle
Order: -20260821
---

# A broken agent tool no longer blocks chat

When one of an agent's optional tool integrations could not start — for example a mailbox tool on
a deployment where email is not configured — the whole agent failed, and every conversation with
it showed an error instead of an answer.

Now the agent simply runs without that one tool: the rest of its capabilities stay available, and
the missing integration is reported to operators in the logs.
