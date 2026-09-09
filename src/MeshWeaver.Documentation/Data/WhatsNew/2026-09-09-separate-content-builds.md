---
Name: Separate content changes from compiled builds
Category: Feature
Description: Shared build selection can keep content releases focused while retaining checks for affected compiled consumers.
Icon: Sparkle
Order: -20260909
---

Content changes can select the compiled tests that actually read their files without rebuilding
unrelated portal hosts. Compiled dependencies, linked content and declared source scans remain
part of validation.

The shared module release lane can select changes since a successful publication. It retains
changes from failed or cancelled releases, including reverted partial publications, and runs the
full build when it cannot establish a safe baseline or the platform changes.

See [Build scope narrowing](/Doc/Architecture/BuildScopeNarrowing) for the selection contract.
