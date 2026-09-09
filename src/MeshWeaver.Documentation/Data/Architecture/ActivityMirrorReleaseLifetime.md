---
Name: Activity Mirror Release Lifetime
Category: Architecture
Description: "Prepare terminal activity cleanup before starting the write, while the writer's service scope is alive."
---

# Activity mirror release lifetime

A terminal activity write releases its unwatched mirror on **completion**, after its final value
has been delivered. Releasing on the value can tear down the write's own upstream subscription
before its terminal signal arrives. That ordering stays unchanged.

Completion can nevertheless arrive after the writer's service scope has retired. Resolving
`IMeshNodeStreamCache` from that scope in the completion callback throws `ObjectDisposedException`.
This was captured in `ActivityLogAppender.Append` during a dependent Monolith test run on
9 September 2026. Passing test assertions did not establish that cleanup was correct.

`ActivityLogAppender.PrepareMirrorRelease` resolves the cache while preparing the write and returns
an action accepting the final status. The completion callback invokes that action without resolving
services. The cache itself owns the disposed-state and subscriber checks: a disposed cache or a
watched mirror is left alone, and a non-terminal activity never triggers release.

The four framework writers use the same preparation step: `ActivityLogAppender.Append`,
`BuildProtocolDriver.FinishActivity`, `CodeNodeType.FailActivity`, and
`ActivityLogLogger.PublishSnapshotLocked`. The existing immediate `ReleaseMirrorWhenFinal` method
remains available for callers with a live scope; it is not the delayed-completion API.

`ActivityReleaseLifetimeTest` captures cleanup from a real hub, retires that hub and its nested
service scope, and then invokes the completion. The original callback fails at the disposed Autofac
scope; the prepared callback succeeds. `TerminalActivityWriterReleaseTest` separately proves that
terminal writes still release unwatched mirrors and that a Running status releases nothing.

This fixes the late service lookup. It does not establish that all activity producers join their
work before teardown, or resolve the separate node-CRUD and concurrent-logon scheduling issues.
