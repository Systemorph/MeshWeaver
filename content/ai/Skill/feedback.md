---
nodeType: Skill
name: /feedback
description: Capture the user's feedback together with WHERE they are (the current page) and WHO they are (name), and file it into the dedicated Feedback space so the team can review it. Type /feedback followed by the feedback.
icon: 📣
category: Skills
order: 13
autoMount: true
---

You are filing a piece of **user feedback**. The user typed `/feedback` followed by what they want to
say. Your job: capture that text **plus the context that makes it actionable** — *where* they were and
*who* they are — and create one **`Feedback`** node in the dedicated top-level **`Feedback`** space.
One node per submission. Do it, then confirm in one sentence — don't interrogate the user.

# 1. Capture WHO and WHERE (you already have both — do NOT ask)

Both come from your prompt context, not from the user:

- **WHERE (`location`)** — the **`node.path`** in the **`# Current Application Context`** block: the page
  the user was on when they gave the feedback (e.g. `ACME/Reports/Q3`). This is the single most useful
  field — it lets a reviewer jump straight to what the feedback is about. If there is no context node,
  leave `location` empty.
- **WHO** — the **`# Current User`** block: use **`User ID`** for `submittedBy` and **`Name`** for
  `submittedByName`. (The framework also stamps the node's `CreatedBy` with the user automatically; you
  set the content fields so the identity travels with an export and shows on the review card.)

# 2. The dedicated Feedback space is platform-provided — just file into it

Feedback lands in ONE shared, top-level space named **`Feedback`**. It is **seeded automatically on
every instance** (space root + a `Public → Contributor` grant so every user — all platform admins
included — can read the board and contribute). So in the normal case it already exists and is already
open for contributions: **skip straight to §3**.

**Only if it is genuinely missing** — e.g. a local dev mesh running WITHOUT static-repo sync — recreate
it yourself with these two nodes (otherwise never touch them):

**(a) the space** — a real `create` (never `update` a bare node into a Space; that skips partition
provisioning). Load the **`/create-space`** skill for the full recipe; the minimum:

```json
{
  "id": "Feedback", "namespace": "",
  "nodeType": "Space",
  "content": {
    "$type": "Space",
    "name": "Feedback",
    "description": "What users tell us — one node per submission, with where they were and who they are.",
    "icon": "📣",
    "body": "Feedback filed through the `/feedback` skill lands here — each entry records the page the user was on and who they are, so we can act on it.\n\n## Contents\n\n@@(\"area/Search\")"
  }
}
```

**(b) let every user contribute** — one `AccessAssignment` granting the **`Public`** subject the built-in
**`Contributor`** role (read + create, but NOT edit/delete others' entries) at the `Feedback` scope.
Granting `Public` propagates to every authenticated user, so anyone can submit — without being able to
tamper with someone else's feedback:

```json
{
  "id": "Public_Access", "namespace": "Feedback/_Access", "name": "All users — Contributor",
  "nodeType": "AccessAssignment", "mainNode": "Feedback",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "Public", "displayName": "All users",
    "roles": [ { "$type": "RoleAssignment", "role": "Contributor" } ]
  }
}
```

`mainNode` MUST equal the scope (`Feedback`) — an empty `mainNode` is silently ignored (see [/access](/access)).

# 3. File the feedback node

Create one `Feedback` node under the `Feedback` space. Give it a short, human `name` (a few words
summarising the feedback — this is the review-list title), and fill the content:

```json
{
  "id": "{short-slug-or-guid}", "namespace": "Feedback",
  "name": "{a few words summarising the feedback}",
  "nodeType": "Feedback", "icon": "📣",
  "content": {
    "$type": "Feedback",
    "text": "{the user's feedback, verbatim — lightly cleaned up, never editorialised}",
    "location": "{node.path from Current Application Context, or empty}",
    "submittedBy": "{User ID}",
    "submittedByName": "{Name}",
    "category": "{optional: bug | idea | praise | question — only if obvious, else omit}",
    "status": "New"
  }
}
```

- Content field names are **camelCase**; `status` serialises by name — start it at **`New`**.
- Put the WHOLE of what the user said in `text`. Don't summarise it away — the `name` is the summary,
  `text` is the record.

# 4. Verify and confirm

- `get @Feedback/{id}` → the node exists with your `text`, `location`, and `submittedByName`.
- Reply in ONE sentence: thank them and link the entry — `Filed your feedback → [{name}](@/Feedback/{id}).`
  Do not dump the JSON back at them.

# Notes

- **Never** invent a location or a name — read them from the context blocks. If a field genuinely isn't
  available (no context node), leave it empty rather than guessing.
- Everyone who can reach the `Feedback` space can read the entries filed there (it's a shared inbox). Don't
  put anything in `text` the user wouldn't want other space members to see; if they flag something private,
  tell them this is a shared space.
