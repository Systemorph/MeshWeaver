---
NodeType: Markdown
Name: "Managing Your Notification Preferences"
Abstract: "Choose, for each kind of notification — approvals, inbox, triage, access grants, finished threads, system — whether it reaches you in the in-app bell, in Microsoft Teams and by email. By default everything reaches the bell and Teams."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#f9a825'/><path d='M12 4a5 5 0 0 0-5 5c0 5-2 6-2 6h14s-2-1-2-6a5 5 0 0 0-5-5z' fill='white'/><path d='M10.5 18a1.5 1.5 0 0 0 3 0' fill='none' stroke='white' stroke-width='1.6' stroke-linecap='round'/></svg>"
Thumbnail: "images/notifications.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "GUI"
  - "Notifications"
  - "Channels"
  - "Preferences"
  - "Settings"
---

# Managing Your Notification Preferences

Memex can tell you when something needs your attention — an approval, a finished thread, a change to a
document you follow. You decide **how** and **where** you hear about it.

## Channels per kind of notification

Open **Settings → Notifications**. There is one section per kind of notification — **Approvals**,
**Inbox**, **Triage**, **Access granted**, **Chat ready**, **System** (and any a module adds) — and each
has three switches:

| Channel | What it does |
|---|---|
| **Notification bell** | The bell in the top bar of the portal. |
| **Microsoft Teams** | A message from the Memex bot in Teams, with a link back to what it is about. |
| **Email** | A message to the address on your profile. |

**If you change nothing, every kind of notification reaches the bell and Teams.** Email is on by
default for approvals and access grants, as it was before. Switch a channel off for one kind and only
that kind changes — for example, keep approvals in Teams but take finished chat threads out of it.

### Connecting Teams

Teams reaches you once you have **sent the Memex bot a message in Teams** — that conversation is where
your notifications arrive. Until then the Teams channel is simply skipped: nothing fails, and the bell
still shows everything. If the portal has no Teams bot configured, Teams is skipped for everyone.

### Approvals on the control instance

When an instance action waits for a second administrator, every administrator who may approve it gets
an **Approvals** notification with a link to the action — so with the default settings it reaches you
in Teams even though you never open that portal's bell.

## Rules — escalation decided by an assistant

**Rules** are the advanced layer on top: plain-English intent a small triage assistant applies to decide
what escalates beyond the bell. Examples:

> - *"Email me approval requests right away."*
> - *"Send me thread completions by email, but nothing about actions I did myself."*
> - *"Don't email me on weekends."*

If you have written rules, the assistant decides about **email** for you (the per-kind email switch then
defers to it); the bell and Teams switches still apply as set.

## The easy way: just ask

You don't have to edit anything by hand. Tell the **Assistant** (or your **Executive Assistant**) in plain
language and it will set it up for you:

> *"Email me when an approval needs me."*
> *"Stop emailing me about thread completions."*
> *"What are my notification settings right now?"*

It will read your current settings, explain them, make the change, and confirm.

## The manual way

If you'd rather manage them directly, the settings are nodes under your own space:

- **Channels per kind:** `{you}/_Settings/Notifications/{kind}` (`approvals`, `inbox`, `triage`,
  `accessGranted`, `chatReady`, `system`) — `bell`, `teams`, `email`.
- **Rules:** `{you}/_NotificationRule/…` — write your intent in `ruleText`; optionally set a structured
  `channel`, plus `enabled` and `order` (lower runs first).
- **Rule channels:** `{you}/_NotificationChannel/…` — `kind` (`Email` / `Teams`), an optional
  `target` address, and `enabled`, for the assistant to route to.

## Tips

- Keep rules short and specific — the assistant interprets the intent, so write them the way you'd tell a
  colleague.
- Use `order` to express precedence when two rules could both apply.
- Disable a channel or rule (set `enabled` off) instead of deleting it if you only want to pause it.
