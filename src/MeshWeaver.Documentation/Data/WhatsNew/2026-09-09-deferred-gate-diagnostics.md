---
Name: Shutdown diagnostics preserve initialization gate names
Category: Fix
Description: Discarded-delivery reports name the gates that held the request even after shutdown has opened them.
Icon: Info
Order: -20260909
---

When a hub shuts down with a request still deferred, its diagnostic and the failure returned to the sender now name the initialization gates recorded when the request was deferred. Shutdown no longer erases those names before the report is written.
