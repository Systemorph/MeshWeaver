---
Name: See at a glance what is public
Category: Feature
Description: There was no way to answer "what can anyone on the internet see?" without fetching the sitemap and reading XML. Administration now lists every publicly readable page.
Icon: Sparkle
Order: -20260810
---

# See at a glance what is public

Some pages here are readable by anyone — course covers, product pages, the store.
Most are not. That difference has always been real and always been enforced: a
page is public because someone granted it, and that grant is what decides whether
a logged-out visitor can open it, whether search engines are told about it, and
whether it gets a preview card when shared.

What was missing was any way to *look* at it. Nothing in the product answered
"what does the outside world actually see?" — the honest way to find out was to
fetch the machine-readable site index and read the raw XML.

**Administration → Published to the web** is that answer, as a list. Every public
page, its public address, what kind of page it is, whether its preview card is one
you made or one drawn for it, and when it last changed.

Two things worth knowing about how it works.

It is built from **the same list search engines get** — not a second list kept
alongside it. A separate list would eventually disagree with the real one, and the
version people read would be the wrong one.

And publishing **moves nothing**. A public page keeps the address it always had.
It was tempting to gather everything public into one section, but that would change
every address — breaking every link anyone has ever shared, and throwing away the
search ranking those pages have earned. Being public is something a page *is*, not
somewhere it *lives*.

The list is deliberately read-only. Making a page public, or stopping it being
public, stays where access is managed — on the page's own Access tab. One place to
change it, one place to read it.
