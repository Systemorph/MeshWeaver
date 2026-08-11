---
Name: Link-preview cards in documents
Category: Feature
Description: A document can now show a link as a proper preview card — picture, title, description, all clickable — instead of a bare URL. One reference renders one card; a list of links renders a responsive card grid. Works for pages on other portals and for nodes on your own.
Icon: PreviewLink
Order: -20260811
---

# Link-preview cards in documents

Paste a link into a chat and you get a nice unfurled card. Put the same link into
one of your documents and you got... a blue underlined URL. Authors who wanted a
card built one by hand — a table of images and links, assembled and maintained
manually, page by page.

Documents can now render any link as a **real preview card**: the page's picture,
its title, its description, the whole card clickable. It is a standard layout
area, embedded the way every layout area is:

```markdown
@@("Your/Doc/area/OgCard?url=https://memex.meshweaver.cloud/Underwriting")
```

And because one card is rarely the point, a comma-separated list renders a whole
**responsive card grid** — sixteen course modules in one line of markdown:

```markdown
@@("Your/Doc/area/OgCard?urls=https://memex.meshweaver.cloud/Underwriting,https://memex.meshweaver.cloud/Pricing,https://memex.meshweaver.cloud/Claims")
```

For a page on another portal, the platform reads the same preview metadata a chat
unfurl reads — fetched once, server-side, and remembered, so a page full of cards
costs the target site one visit, not one per reader. For a node on your own
portal, no fetching is needed at all: the card binds to the node itself and stays
live — rename the node and the card follows.

```markdown
@@("Your/Doc/area/OgCard/Edu/Underwriting")
```

Cards for pages that cannot be reached don't break the page: they render as a
plain named card that still links where it always did.
