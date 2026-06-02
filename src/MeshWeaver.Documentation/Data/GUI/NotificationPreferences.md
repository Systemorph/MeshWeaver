# Managing Your Notification Preferences

Memex can tell you when something needs your attention — an approval, a finished thread, a change to a
document you follow. You decide **how** and **where** you hear about it.

## The two pieces

**Channels** — *where* notifications can go. Everyone has the **in-app bell** (always on). Add more
channels to receive notifications elsewhere:

| Channel | What it does |
|---|---|
| **In-app** | The bell in the top bar. Always on — you can't turn it off. |
| **Email** | Sends the notification to your mailbox. Add this to get emails. |
| **Teams** | (Coming soon) Sends to Microsoft Teams. |

**Rules** — *which* notifications escalate beyond the bell, written in **plain English**. A small,
fast assistant reads your rules and decides, for each notification, whether to also send it to email
(or Teams). Examples of rules you can write:

> - *"Email me approval requests right away."*
> - *"Send me thread completions by email, but nothing about actions I did myself."*
> - *"Don't email me on weekends."*

## How it works

1. The **in-app bell** always fires — that's the default, and it costs nothing.
2. If you've written **rules**, the triage assistant checks each notification against them and escalates
   to the channels you asked for.
3. **No rules = in-app only.** To start getting emails you need **both**: an **Email channel** *and* a
   **rule** that says what to send there.

## The easy way: just ask

You don't have to edit anything by hand. Tell the **Assistant** (or your **Executive Assistant**) in plain
language and it will set it up for you:

> *"Email me when an approval needs me."*
> *"Stop emailing me about thread completions."*
> *"What are my notification settings right now?"*

It will read your current settings, explain them, make the change, and confirm.

## The manual way

If you'd rather manage them directly, the settings are stored as nodes under your own space:

- **Channels:** `{you}/_NotificationChannel/…` — set `kind` (`InApp` / `Email` / `Teams`), an optional
  `target` address (defaults to your own), and `enabled`.
- **Rules:** `{you}/_NotificationRule/…` — write your intent in `ruleText`; optionally set a structured
  `channel`, plus `enabled` and `order` (lower runs first).

## Tips

- Keep rules short and specific — the assistant interprets the intent, so write them the way you'd tell a
  colleague.
- Use `order` to express precedence when two rules could both apply.
- Disable a channel or rule (set `enabled` off) instead of deleting it if you only want to pause it.
