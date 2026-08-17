---
Name: Pages with comments and approvals stay reachable after updates
Category: Fix
Description: A page whose configuration enabled comments or approvals could become permanently unreachable after a platform update — its hub failed to start and every request to it timed out. Registration is now idempotent, so the page always comes up.
Icon: CheckmarkCircle
Order: -20260817
---

# Pages with comments and approvals stay reachable after updates

Some pages enable comments and approvals in their own configuration while the platform also
enables them by default. After the approvals module was extracted, that double registration could
make the page's hub fail to start — the page never loaded, and everything sent to it (including
its tests) waited forever instead of failing with an error.

The registration is now idempotent: enabling comments or approvals a second time is recognized
and collapsed, and even when two registrations land through different doors they merge instead of
colliding. What you notice: pages that request approvals or carry comment threads keep loading
normally through platform updates, and gate runs against such content finish instead of timing
out.
