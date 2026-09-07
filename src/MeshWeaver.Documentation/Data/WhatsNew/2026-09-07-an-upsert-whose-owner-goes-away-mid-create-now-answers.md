---
Name: An upsert whose owner goes away mid-create now answers
Category: Fix
Description: Creating something could leave you waiting with no answer at all if the thing that owned it was restarted at that moment. You now get a clear refusal saying it was not created and can be retried, instead of silence.
Icon: PlugDisconnected
Order: -20260907
---

# An upsert whose owner goes away mid-create now answers

Creating a node could hang for as long as the caller was willing to wait, in complete silence.
Installing a plugin package briefly retypes and then restarts the thing that owns the package — that
happens on every install — and a create caught in that moment simply never received a reply. Nothing
was logged, nothing failed, and the caller waited.

The create now answers either way. When the reply cannot arrive you get a refusal that says the
create was **not** applied and is safe to retry, and names the restart as the usual reason.
