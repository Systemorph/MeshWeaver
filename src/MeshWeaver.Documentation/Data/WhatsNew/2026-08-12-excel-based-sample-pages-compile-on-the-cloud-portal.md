---
Name: Excel-based sample pages compile on the cloud portal
Category: Fix
Description: Node types whose code loads spreadsheet data now compile on the distributed portal, not only in local development.
Icon: Sparkle
Order: -20260812
---

# Excel-based sample pages compile on the cloud portal

Code stored in a node is compiled by the portal at runtime, against the libraries that portal ships.
The distributed portal did not ship the spreadsheet-import library, although the local development
portal did — so sample pages whose code loads data from a workbook compiled fine on a developer's
machine and failed in the cloud with an error about a missing `MeshWeaver.Import` namespace.

The library now ships with the distributed portal too, so those pages compile in both places. This
also makes the failure mode less surprising in general: what in-node code can reference is whatever
the running portal ships, not whatever exists in the framework.
