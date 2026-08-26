---
Name: Documentation images and the Microsoft 365 connect link
Category: Fix
Description: Fixes broken images on documentation pages and the "Connect Microsoft 365" link, which returned a server error on portals with mail turned off.
Icon: Sparkle
Order: -20260825
---

# Documentation images and the Microsoft 365 connect link

Images and diagrams on documentation pages could fail to load, leaving a broken-image placeholder
on an otherwise complete page. The page's assets are stored with the page itself, but the portal
lost track of where they lived when it looked them up, so it could not read them back. It now
carries that information through, and the images load.

The "Connect Microsoft 365" link that starts the Executive Assistant's consent flow returned a
server error on any portal that does not have system email enabled. It now works as intended — and
where the Microsoft credentials have not been set up, it says so plainly instead of failing.

Portal operators get one new line in the startup log stating whether semantic (meaning-based)
search is switched on, and when it is not, which setting would switch it on. Search results were
already falling back to plain text matching in that case; nothing said so, which made a working
portal look broken.
