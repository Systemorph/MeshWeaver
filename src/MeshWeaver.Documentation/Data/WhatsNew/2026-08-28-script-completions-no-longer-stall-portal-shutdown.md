---
Name: Script completions no longer stall portal shutdown
Category: Fix
Description: The first code-completion or script run in a portal's lifetime could hold up a restart for up to 30 seconds; it no longer does.
Icon: Sparkle
Order: -20260828
---

# Script completions no longer stall portal shutdown

The very first time a portal process ran a script cell or offered code completions in a
script-flavored editor, it had to build a shared list of reference assemblies — a one-time cost
that is normally quick, but occasionally ran long under memory pressure. That build used to run
inline while holding a slot in the pool a shutdown needs to hand back cleanly, so on the rare
occasion it was slow, a restart or redeploy could sit waiting on it for up to 30 seconds instead of
completing promptly.

The build now runs independently of any one request: a request waits for it but no longer blocks a
shutdown from proceeding, even on that first, slower call. Restarts and redeploys are consistently
quick regardless of when they land relative to the first script or completion request.
