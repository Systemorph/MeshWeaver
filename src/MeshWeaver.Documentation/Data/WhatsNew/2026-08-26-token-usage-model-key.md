---
Name: Token usage is attributed to one model, not several spellings
Category: Fix
Description: A model selected from the picker could have its token usage recorded under a node path instead of the model id, splitting one model across several rows in the usage report.
Icon: Database
Order: -20260826
---

# Token usage is attributed to one model, not several spellings

Token usage is recorded per model on each conversation. When a model was picked from the dropdown
and the provider did not report back which model answered, the usage was filed under the model's
node path rather than its identifier — so the same model showed up as two separate rows in the
token-usage report, and the row keyed by path could not be priced. Usage is now always recorded
under the model's identifier, whichever way the model reached the conversation.
