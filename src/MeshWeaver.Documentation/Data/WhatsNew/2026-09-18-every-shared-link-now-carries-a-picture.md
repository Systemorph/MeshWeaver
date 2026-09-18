---
Name: Every shared link now carries a picture
Category: Improvement
Description: A page shared into iMessage, Slack, Teams or LinkedIn used to unfurl as a bare title beside the tiny site favicon. The card the portal draws for a page now shows the page's own mark, its description, its category, its price and where it lives — and a page with no mark of its own gets a badge, so no link ever shares as text on a dark rectangle.
Icon: Image
Order: -20260918
---

# Every shared link now carries a picture

Paste a MeshWeaver link into a chat and the receiving app fetches the page once, reads what it
says about itself, and draws a card. Until now that card was thin: the Store shared into iMessage
as the word "Store" beside a favicon the size of a fingernail, because the picture the page offered
was a dark rectangle with the title on it and nothing else, and the page did not say how big that
picture was — which is what several apps decide by.

Three things change.

**The card shows what the page knows about itself.** The page's own mark is drawn large on the
right, its category sits above the title, its description below it, a price appears as a chip when
the page sells something, and the footer says which instance and which path the link points to. A
store root that has a headline and a tagline but no description now shows the tagline — it was
blank before.

**A page with no mark still gets a picture.** A rounded badge in the card's colour, carrying the
page's initial, stands in for a mark the page never authored. Two such pages still share with two
different pictures.

**Every page has a card — not only public nodes.** The home page and any route that is no public
node share as the instance itself: its name and host on the same card. Nothing about a private page
reaches that card; it says only what the site already says about itself. And every card now
declares its type and size in the page's head, with the same picture mirrored for the apps that
read the `twitter:*` tags, so a receiving app can show the large layout without a second fetch.

Nothing about *who may see a page* changes. The card is drawn only for a page a stranger may open,
by the same rule that decides whether the page itself is served to one.
