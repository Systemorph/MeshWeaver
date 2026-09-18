---
nodeType: WhatsNew
Name: A section that cannot render says so in your language
Category: Fix
Description: "When part of a page had no renderer, the page showed a framework diagnostic - the internal area name, the hub address and a list of sixty area names - in the middle of the content, in English, whoever was reading. It now leads with one sentence in the reader's language and folds the technical detail away underneath."
Icon: DocumentError
Order: -20260917
---

A page can embed sections that other packages render — the approvals on a document, its signatures.
When one of those cannot be rendered by the server that answered your request, the page has to say
something.

What it said was written for whoever maintains the platform: **"No renderer is registered for area
`Approvals` on hub `Approvals/Workspace`"**, followed by the sixty-odd internal names of every area
that *is* registered. In English, whoever was reading. On 2026-09-17 that landed in the middle of a
letter to a customer.

**It now leads with one sentence, in the reader's language** — that the section could not be
displayed, and that the rest of the page is unaffected — with the technical detail folded away
beneath it for whoever needs it. Nothing is lost: the area, the hub and the list of what is
registered are all still there, one click away, and still in English, because that half is for an
operator reading a log or a bug report rather than for the person reading the page.

The sentence deliberately does not guess at the cause. A section can fail to render because a
package is not loaded on that particular server, because an area was renamed, or because a page
refers to one that never existed — and those need different fixes. Telling you the rest of the page
is intact is the part that is always true.
