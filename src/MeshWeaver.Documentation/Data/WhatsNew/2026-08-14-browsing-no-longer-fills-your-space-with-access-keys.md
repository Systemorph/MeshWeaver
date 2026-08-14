---
Name: Browsing no longer fills your space with access keys
Category: Fix
Description: Every page view used to create two permanent access keys in your own space, and nothing ever removed one. Pages are now read with the sign-in you already have, and keys that have expired are cleared away.
Icon: Key
Order: -20260814
---

# Browsing no longer fills your space with access keys

An access key is the credential a program uses to talk to the portal on your behalf — the thing you
create once, paste into a tool, and forget about. It lives in your space as a real item, because
you need to be able to see it and take it back.

The new frontend was creating one on every page view.

Not deliberately. When the server prepares a page for you it has to read the page's content, and
the way it asked for that content required an access key — so it made one, used it for that single
render, and dropped it. Dropping it did nothing: the key stays. Each one is two items (the key
itself and the entry that lets the portal find it), so twenty pages of ordinary reading left forty
permanent credentials behind. A search engine crawling the site left far more. Nobody sees them
until they open their key list and find hundreds of entries they never made.

The mistake was asking for the wrong credential. The server preparing your page already holds your
sign-in — it is your browser's request it is answering. It never needed a second credential to read
something on your behalf; it only needed permission to *read*, which your sign-in already grants.
Reading a page now uses it, and creates nothing.

Reading is the whole of it, deliberately. Your sign-in is enough to look at a page and to ask who
you are; it is not enough to create, change, delete, or run anything. Those still require a real
access key, so widening what a page render can do was never on the table.

The keys already piled up are cleared as they expire. A key made for a browser session expires
after a day, and until now nothing ever removed the expired ones — they simply accumulated as dead
credentials in your list. Now, whenever a new key is created for you, any of your own keys whose
expiry has already passed are removed along with their lookup entries. Keys with no expiry date —
the ones you made yourself for a tool you are still using — are never touched, and neither are keys
belonging to anyone else.
