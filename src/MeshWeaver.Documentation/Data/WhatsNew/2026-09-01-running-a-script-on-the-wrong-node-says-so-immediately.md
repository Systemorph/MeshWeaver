---
Name: Running a script on the wrong node says so immediately
Category: Fix
Description: ExecuteScript now checks the target before it dispatches, so a path that does not exist — or a node that is not an executable Code node — is answered at once, in words you can act on, instead of waiting out the request budget for a reply that was never coming.
Icon: Play
Order: -20260901
---

# Running a script on the wrong node says so immediately

`ExecuteScript` sends a run request to the node you name and waits for that node's own hub to
answer. When the target is a real, executable `Code` node, the answer arrives in milliseconds and
nothing about this changed.

When it is not, the tool used to wait for a reply that nobody was going to send. In the worst case
observed in production, an agent asked to run a path that did not exist and sat there for a full
minute before reporting a timeout — a timeout whose own wording could not say whether the request
had been undeliverable or delivered to something that simply never replied. Agents retry, so one
mistyped path could cost several minutes and leave a trail of red log lines behind it.

Two of the reasons a target cannot run a script are knowable up front, so they are now established
up front, from a single bounded read of the node:

- **`NodeNotFound`** — there is no readable node at that path.
- **`NotExecutable`** — the node is there, but it carries no code, or its `isExecutable` flag is
  off.

Both come back immediately, and both name the condition rather than an exception class, so the
answer tells you what to fix: correct the path, or point at a `Code` node and turn execution on.
Nothing is dispatched in either case, so nothing can have happened as a side effect.

The check is deliberately one-sided. If the read cannot reach a verdict — the node is momentarily
unreachable rather than missing — the tool does **not** refuse. It dispatches exactly as before and
lets the owning node have the last word. A check that fired on "I could not find out" would turn a
brief hiccup into a hard error on a script that was perfectly fine, which is the more expensive
mistake by far.

The tool's reply is also documented as what it has always been: an acknowledgement that the run
**started**, carrying the activity to watch for the result — not the result itself.
