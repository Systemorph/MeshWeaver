---
nodeType: Skill
name: /navigate
description: Take me there — open a node, doc, or page in the UI. Pane-aware (opens in the pane opposite the thread) and resilient (finds the best match even if the path is off).
icon: Location
category: Skills
order: 3
action:
  kind: Navigate
---

Navigate the user to a place in the UI. Resolution is **resilient** and application is **pane-aware**.

## Resolution — direct path first, else make sense of the row

- **One argument that looks like a path** (`/navigate Doc/AI/ModelProviderSettings`, `/navigate @/rbuergi`, a pasted URL): resolve it as a **direct path** first. The URL is corrected automatically — a leading `@`, a stray `/node/` segment, a pasted `https://…host/…` prefix, doubled slashes, and percent-encoding are all cleaned up. If the exact node isn't there, fall back to the **best search match** rather than dead-ending.
- **Free text** (`/navigate model settings`, `/navigate my notifications`): make sense of the context on the row — match it to a **skill** where one fits (so the user can *do* something, e.g. `/model` to change the model), otherwise **search** the mesh and open the best-matching node.

Never report success for a place that doesn't exist. If nothing resolves, say so and offer the closest matches.

## Application — always the opposite pane

- **Thread is in the MAIN pane** → open the target in the **side panel** (so the conversation and its subject sit side by side).
- **Thread is in the SIDE panel** → **change the URL and navigate the MAIN pane**.

App routes with a query string (e.g. a `/search?…` results page) are pages, not renderable nodes — those always navigate the main view.

## Prefer skills over raw routes

When a user asks to *do* something ("change my model", "set up a provider", "manage access"), route them to the matching **skill** (`/model`, `/provider-keys`, `/access`) — a skill can navigate **and** carry out the task — rather than dropping them on a bare search URL.
