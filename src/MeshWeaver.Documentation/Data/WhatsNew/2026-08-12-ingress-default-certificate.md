---
Name: Certificate warnings on corporate networks fixed
Category: Fix
Description: Strict corporate security proxies no longer flag the portal as insecure, because the platform now presents its real certificate to every connection.
Icon: Sparkle
Order: -20260812
---

# Certificate warnings on corporate networks fixed

Some users on strictly managed corporate networks saw browser security warnings when opening the portal, and in some cases their company firewall blocked access entirely. The platform now always presents its real, publicly trusted certificate — including to the automated security scanners corporate networks use — so these warnings no longer occur. If your organization still blocks the portal, ask your IT department to re-scan the site.
