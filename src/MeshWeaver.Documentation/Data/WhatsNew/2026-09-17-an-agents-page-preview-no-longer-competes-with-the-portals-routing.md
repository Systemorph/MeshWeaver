---
Name: An agent's page preview no longer competes with the portal's routing
Category: Fix
Description: When an agent or an MCP client asked to render a page, the live connection it opened was held by the component whose only job is passing messages between all the others — and every frame of that page was handed back to it. The connection now runs on its own component.
Icon: Document
Order: -20260917
---

# An agent's page preview no longer competes with the portal's routing

Inside an installation, one component is the **router**: it exists to pass messages between all the
others, and nothing else. Work handed to it directly competes with that job, and under load the
installation stops answering.

When an agent — or any MCP client — asks the portal to render a page, the portal does exactly what a
browser does: it opens a **live connection** to the component that owns that page and waits for the
first complete picture. Opening that connection is a request, and the component that sent it becomes
the address every later frame is delivered to. For a render asked for by an agent, that component
was the router.

So the router was not merely passing the request along. It was the *recipient* of the whole
conversation: the acknowledgement, every update the page produced, and the notice that the page had
finished — all addressed straight back at it, for as long as the preview was open.

## What changes

The connection is now opened by a **component dedicated to exactly this** — holding live page
connections and delivering their frames. The router goes back to only routing.

Nothing about the rendered page changes: same content, same permissions, same identity doing the
reading, same budget. A portal page, a browser session and an MCP session were never affected —
each of those already opened its connection on its own component, and for them this is precisely
what was already happening.

## Why not simply reuse the existing read path

The installation already has a component for one-off reads, and that was the obvious candidate. It
is deliberately built to answer *nothing* — it has no handlers at all, so that the only thing it
ever does is hand back the answer to a read someone is waiting for. That is exactly what makes it
the wrong home for a live connection: the frames of a page are not an answer to a request, they
arrive on their own schedule, and a component with no handlers has nowhere to put them. They would
have gone nowhere, silently.
