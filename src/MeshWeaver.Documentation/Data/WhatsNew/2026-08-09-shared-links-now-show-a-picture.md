---
Name: Shared links now show a picture
Category: Fix
Description: Pasting a link to a public page into Slack, LinkedIn or a message produced a bare line of text. Every public page now has a proper preview card — one it draws for itself if nobody made one.
Icon: Sparkle
Order: -20260809
---

# Shared links now show a picture

Paste a link to a course or a product page into a chat, a post or an email, and
you got a bare underlined URL. No picture, no card — the sad grey rectangle, or
nothing at all. Meanwhile several of those pages had a hand-made preview image
sitting right there in their own folder, going unused.

Two things were wrong, and the second one is the interesting one.

The pages that *did* have a preview image were storing it under one name while
the part of the platform that writes the preview tag was looking for a different
one. The two never met, so the tag was simply never written — which is why not a
single product or course page has ever had a preview picture, no matter how much
care went into making one. They are all connected up now.

The bigger problem was that having a picture at all was **optional**. A page only
got a preview if somebody had remembered to make one, and most pages had nobody
to remember. So a preview is no longer something a page opts into: when a page
has no picture of its own, the platform now **draws one for it** — the page's
name, its short description and its category, set on a dark card with a colour of
its own. Every public page has one, immediately, with nothing to do. If you later
add your own image, yours wins.

The colour is picked from the page's own identity, so the same page always shares
in the same colour and a row of links from different pages reads as a set rather
than a smear. Cards are drawn on demand and cached, so this costs nothing until
somebody actually shares something.

Only pages that are already public get a card. Anything that needs a sign-in is
untouched — a page nobody may read anonymously does not get a preview, and its
name cannot be pulled out of the preview address either.
