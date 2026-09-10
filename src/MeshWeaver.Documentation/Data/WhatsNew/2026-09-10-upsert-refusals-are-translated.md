---
Name: Refused writes now explain themselves in your language
Category: Fix
Description: When a create-or-update is refused, the reason in the activity transcript is now shown in the viewer's language instead of always English.
Icon: LocalLanguage
Order: -20260910
---

A write that is refused writes the reason into the activity transcript, and that transcript is read in the language of whoever opens it. Until now only the *confirmations* worked that way — "Created node at …" appeared in German for a German viewer, while every refusal appeared in English for everyone.

All fifteen refusals the create-or-update path can produce are now translated, including the ones composed elsewhere and handed to it: a NodeType that does not resolve, an existence check that could not reach a verdict, an access grant whose scope does not match the folder it sits in, and a write that finished without confirming.

Refusals are translated as whole sentences with the details filled in, so a German transcript reads in German word order rather than as a translated frame around English fragments. Where a message is the verbatim output of something else — an exception, a compiler diagnostic — it is shown as it came, in every language, because translating it would misrepresent what was reported.
