---
nodeType: Skill
name: /feedback
description: Capture the user's feedback with WHERE they are (the current page) and WHO they are (name), and file it PRIVATELY under their own space — only they and platform admins can see it. Type /feedback followed by the feedback.
icon: 📣
category: Skills
order: 13
autoMount: true
---

You are filing a piece of **user feedback**. The user typed `/feedback` followed by what they want to
say. Capture that text **plus the context that makes it actionable** — *where* they were and *who* they
are — and create one **`Feedback`** node under the user's OWN space. One node per submission. Do it,
then confirm in one sentence — don't interrogate the user.

# 1. Capture WHO and WHERE (you already have both — do NOT ask)

Both come from your prompt context, not from the user:

- **WHERE (`location`)** — the **`node.path`** in the **`# Current Application Context`** block: the page
  the user was on (e.g. `ACME/Reports/Q3`). The single most useful field — it lets a reviewer jump
  straight to what the feedback is about. If there is no context node, leave `location` empty.
- **WHO** — the **`# Current User`** block: **`User ID`** and **`Name`**. The `User ID` is BOTH where the
  node lives (§2) and the `submittedBy` field; `Name` is `submittedByName`.

# 2. File it under the user's OWN space — `{User ID}/Feedback/{id}`

Feedback is **private to the submitter**. It lives under the user's own partition, so **only they — and
platform admins, who review everything — can see it**; no other user can. You need **no grant and no
shared space**: a user owns their own namespace automatically (the self-scope rule), so the create just
works under the calling user.

Create the node with **`namespace = "{User ID}/Feedback"`** (the `User ID` from the `# Current User`
block — this prefix is what makes it private):

```json
{
  "id": "{short-slug-or-guid}", "namespace": "{User ID}/Feedback",
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

- The `namespace` MUST start with the user's own **`User ID`** — that is the entire privacy boundary.
- Content field names are **camelCase**; `status` serialises by name — start it at **`New`**.
- Put the WHOLE of what the user said in `text` — the `name` is the summary, `text` is the record.

# 3. Verify and confirm

- `get @{User ID}/Feedback/{id}` → the node exists with your `text`, `location`, and `submittedByName`.
- Reply in ONE sentence, thanking them and linking the entry:
  `Filed your feedback → [{name}](@/{User ID}/Feedback/{id}).` Don't dump the JSON back at them.

# Notes

- **Never** invent a location or a name — read them from the context blocks; leave a field empty rather
  than guessing.
- Feedback is **private**: only the submitter and platform admins can read it. If a user worries their
  feedback is sensitive, reassure them it is not visible to other users.
- **You don't route it anywhere.** Platform admins review all submitted feedback in the admin **Feedback**
  review (their Settings) — filing the node under the user's space is the whole job.
