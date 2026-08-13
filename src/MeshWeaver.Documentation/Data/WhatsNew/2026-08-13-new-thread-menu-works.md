---
Name: The ➕ New thread menu entry works again
Category: Fix
Description: Clicking ➕ New thread in the AI menu now opens the composer in the main view and collapses the side panel — it previously did nothing.
Icon: Chat
Order: -20260813
---

# What's New — 13 August 2026

## The ➕ New thread menu entry works again

Clicking **➕ New thread** in the AI (✨) menu did nothing — no navigation, no composer, no error. The click handler resolved the signed-in user from an identity slot that is only populated while the server is processing a mesh message, not while it is handling a browser click, so it always concluded "no user" and silently gave up.

The handler now resolves the user from the circuit's durable identity — the same source every chat surface uses. Clicking **➕ New thread** opens a fresh conversation composer in the main view and collapses the side panel, so the new conversation exists exactly once, in front of you.

The same repair applies to the two neighbouring header actions that resolved the user the same broken way: the settings button's route to your account page, and the threads shortcut to your activity dashboard.
