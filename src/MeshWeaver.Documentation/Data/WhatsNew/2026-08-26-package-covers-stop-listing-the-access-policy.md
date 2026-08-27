---
Name: Package covers stop listing the access policy as their content
Category: Fix
Description: A package's public cover page listed its internal access policy under Contents — on packages that ship no browsable nodes, as the only entry. Governance nodes are now hidden from content listings everywhere, not just on some queries.
Icon: ShieldTask
Order: -20260826
---

# Package covers stop listing the access policy as their content

The **Contents** section of a package's public page listed **Access Policy** — the package's own
internal security record. On the thirteen packages that ship no browsable nodes of their own it was
the *only* entry, so the first thing a visitor saw under "Contents" was a piece of internal
bookkeeping.

Node types have always been able to declare that they do not belong in a page's contents, and the
access policy has declared exactly that from the start. The declaration was simply never reaching
the database on a portal whose data is split across partitions: the component that answers a
"what is inside this page?" question was being handed no list of what to hide. The same was true of
mesh-wide listings that span every partition.

Both now carry the declaration, so a type that opts out of content listings is hidden from them
consistently — the access policy, install ledgers, sync configurations, token-usage records and the
rest — regardless of which page you are on or how the listing is scoped.
