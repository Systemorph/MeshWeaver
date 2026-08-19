---
Name: Delegations now find your own agents
Category: Fix
Description: Agent-to-agent delegations run under your identity, so agents from your personal partition resolve instead of failing "not found".
Icon: Sparkle
Order: -20260818
---

# Delegations now find your own agents

When an agent delegated work to another agent on your behalf, the delegated task used to run
without your identity. That meant agents you keep in your own partition (for example
`you/Agent/post-writer`) were invisible to the delegated task — it failed with "agent not
found" even though the agent was offered and picked correctly.

Delegated tasks now carry your identity end to end: the task is dispatched as you, and the
agent catalog the delegated task sees includes your personal agents — the same set you see in
the picker. Delegating to your own specialized agents works reliably, including from nested
sub-tasks.
