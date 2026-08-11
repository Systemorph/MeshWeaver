---
Name: A list of links renders one card each again
Category: Fix
Description: Embedding several links in one preview-card reference produced a single broken card instead of one card per link. Every form of the list now splits correctly, and a card that cannot be reached names the page it points at instead of just the domain.
Icon: PreviewLink
Order: -20260811
---

# A list of links renders one card each again

Preview cards are meant to be embedded as a list — that is the whole point of
the grid. Four links in one reference, four cards.

Instead you got **one** card, and a broken one: the four addresses were glued
together into a single address that leads nowhere, so nothing could be loaded
for it. What rendered was a lone card labelled with just the site's name — for
all the world a broken link to the portal itself.

The cause was narrow. A card list can be written two ways: as a query on the end
of the reference, or as part of the reference's own path. The query form split
its list on the commas correctly; **the path form never split at all** and
treated the whole line as one address.

Both forms now split, and they accept the separator whichever way it arrives —
plain, or in the escaped shape a link picks up when the reference is encoded as
one piece. If you genuinely need a comma *inside* one address, write it doubly
escaped, or use the single-link form, which never splits.

While fixing it, one related annoyance went too. A card whose page cannot be
reached used to fall back to showing just the domain, so a row of unreachable
cards all read alike and none said where it led. Such a card now shows the page's
own name — *Reinsurance*, *Underwriting* — and still links exactly where it
always pointed, so what you read and where you land agree.
