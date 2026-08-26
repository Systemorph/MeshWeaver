---
Name: Governance nodes stop listing as page content
Category: Fix
Description: Access policies, install ledgers and other internal bookkeeping no longer appear under Contents or in mesh-wide search on the pages they belong to.
Icon: ShieldTask
Order: -20260826
---

# Governance nodes stop listing as page content

Internal bookkeeping filed alongside a page — its access policy, an install ledger, a sync
configuration — no longer shows up as if it were content of that page. It had been appearing under
**Contents**, and turning up in search results across the whole workspace, because each of these
nodes was created without recording which page it belongs to.

An earlier fix corrected the five built-in catalogs (Agents, Skills, Models, Harnesses,
Documentation). This one covers the rest: the Roles, Licenses and Settings catalogs, the second
Documentation entry, the access policy you create from the **Set partition policy** dialog, and the
plugin install ledger.

The platform now works this out for itself whenever such a node is saved, so bookkeeping created
from here on is filed correctly without anyone having to remember. Existing entries in the built-in
catalogs correct themselves the next time the deployment starts.
