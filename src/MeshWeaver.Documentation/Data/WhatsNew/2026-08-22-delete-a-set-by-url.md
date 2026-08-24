---
Name: Delete by link — a single node or a whole query result
Category: Feature
Description: Every node's Delete page has a clear URL, it can now take mesh queries to delete a whole set after review, and a refused delete tells the agent to hand you that link instead of retrying.
Icon: Sparkle
Order: -20260822
---

# Delete by link — a single node or a whole query result

Every node's delete confirmation lives at a clear URL — `/{path}/Delete` — where you see what the
deletion covers (descendants included) and confirm by typing DELETE. New: that page can now name a
whole **set** of nodes. Append `?q=` with one or more mesh queries and the page lists exactly what
matches — only what you can read — before anything happens. Deletions then run one at a time under
**your** identity: anything you may not delete is refused by the server and listed by path, never
silently skipped.

This closes the loop with agents. An agent's identity often holds no Delete on shared or
system-synced spaces — correctly so. A refused agent delete now says so explicitly and carries the
delete link, and the agent guidance tells it to present that URL to you rather than retry: you open
the page, review the set, and decide with your own rights.

Two smaller sharpenings in the same area: a delete refused from a card's trash affordance now shows
you the refusal instead of doing nothing quietly, and the row-delete in collection views asks for an
inline confirmation first — a destructive action never runs off a single stray click.
