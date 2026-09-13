---
Name: A public root cannot fall out of the sitemap — the root filter runs in storage, uncapped
Category: Fix
Description: The sitemap found its public roots by asking for the first 500 mains of each candidate type mesh-wide and keeping the top-level ones afterwards. Once a type has more than 500 mains — every course installed into a user's space is one — which roots made the window was up to storage order, and a public site could silently drop out. The root filter now runs in the query, and the enumeration is uncapped.
Icon: Map
Order: -20260913
---

The sitemap lists every page a logged-out visitor may open, below every public root. To find those
roots it asked the mesh for `nodeType:Space is:main limit:500` (and the same for store plugins and
catalogs), then kept the results whose path had no `/`. That is a window applied before the
filter: with more than 500 `Space` mains on the mesh — every course installed into a user's space
is one, every store plugin copy is one — which top-level roots landed inside the first 500 was
decided by the storage's order, and a public root that did not make the window vanished from the
sitemap. Nothing errored; the sitemap just listed fewer URLs, which is exactly what made it
invisible. Measured on memex.meshweaver.cloud with 13 `Space` mains, so the window had not been
hit yet — this was fixed before it bit.

The query language already pushes a root filter down — `namespace:` with an empty value is
`namespace = ''` on every backend, the same shape the home page's root leg uses — so the sitemap
now asks for top-level mains only, with no cap: the population is bounded by the number of
partitions, not by the number of mains. A test plants 520 non-root spaces ahead of one public root
and asserts the root is published; on the old enumeration the sitemap came back empty.
