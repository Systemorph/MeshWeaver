---
Name: Error alerting recovers instead of parking
Category: Fix
Description: A red-log incident whose triage never came back is now marked Failed and retried, instead of sitting at "Triaging" forever.
Icon: Sparkle
Order: -20260810
---

# Error alerting recovers instead of parking

Automatic error alerting watches production logs, groups each distinct error into
an incident, and hands it to a triage agent that drafts the ticket. If that agent
could not be reached — it was missing from the deployment, its model was down, or
the portal restarted mid-round — the incident marked itself **Triaging** and stayed
there. Nothing could move it: "Triaging" looks like work in progress, so it was
never retried, and no ticket was ever filed for that error. On our own portal
nineteen incidents accumulated that way, silently.

An incident that is waiting on a triage round is now checked against that round.
Once the round is over and nothing came back, the incident is recorded as **Failed**
with the reason — naming the agent it was waiting for and linking the thread — and a
failed incident is retried the next time the same error is seen. So alerting heals
itself as soon as whatever was missing is back, instead of needing a human to notice
that the queue stopped moving.

A successful triage is never disturbed by this: if the agent did write a draft, the
incident goes on to file its ticket exactly as before.
