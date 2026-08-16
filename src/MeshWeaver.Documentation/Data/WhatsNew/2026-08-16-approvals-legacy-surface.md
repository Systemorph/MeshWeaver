---
Name: Approvals keep working in existing content
Category: Fix
Description: Content that wires up approvals with the original AddApprovals call compiles again — the surface was restored after the Approvals module extraction removed it.
Icon: Sparkle
Order: -20260816
---

# Approvals keep working in existing content

The Approvals feature recently moved into its own module. That move accidentally removed the
original programming surface that existing content in several workspaces still uses, so those
pages stopped compiling on the newest version and the update was held back by the platform's
safety gate. The original surface is restored (and now covered by a test), so existing content
keeps working unchanged while new content can use the module directly.
