---
Name: A newly created agent shows up right away
Category: Fix
Description: Creating an Agent node now makes it selectable immediately, instead of leaving it invisible to the agent list until the node was recycled.
Icon: Bot
Order: -20260819
---

# A newly created agent shows up right away

Create a new agent and it is immediately available to pick and to start a thread
with. Previously some newly created agents were simply absent from the agent list —
starting a thread with one failed with "not found among the available agents", and
the error helpfully listed every *other* agent sitting in the same place. Editing the
agent did not help; only recycling the node did, which made it look like something
was holding on to a stale list.

Nothing stale was involved. The agent was being found and delivered correctly the
whole time, then dropped at the last step: the code that turns a stored agent into a
list entry recognised only some of the forms an agent's settings can be stored in,
and quietly discarded the rest. Recycling the node happened to rewrite those settings
into a form it did recognise, which is why it looked like a cache.

Agents are now read in whatever form they were stored, so an agent created through
any route — the editor, an import, the API — appears the moment it exists. The same
correction applies to the model list, which read models the same narrow way.
