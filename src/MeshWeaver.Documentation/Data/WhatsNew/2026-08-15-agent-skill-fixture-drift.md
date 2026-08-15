---
Name: Notification triage uses the intended light model tier
Category: Fix
Description: The built-in NotificationTriage agent now runs on the light model tier as designed, and a stale built-in /feedback skill that filed unusable feedback nodes was removed.
Icon: Sparkle
Order: -20260815
---

# Notification triage uses the intended light model tier

The built-in NotificationTriage agent had drifted from its served master and ran on the chat
model tier; it now runs on the light tier as designed, making notification triage cheaper with
identical results. A stale built-in `/feedback` skill was also removed: it filed feedback nodes
of a type the platform no longer ships, so submissions were silently unusable. Feedback is owned
by the Feedback module, which provides the current draft-and-submit `/feedback` flow.
