---
Name: Instant slide navigation and flicker-free page switches
Category: What's New
Description: Switching slides (and navigating between pages in general) no longer blanks the screen with spinners — the previous content stays visible until the next one is ready.
Icon: Sparkle
---

# Instant slide navigation and flicker-free page switches

Presenting a deck is now smooth: moving between slides no longer replaces the page with a
progress bar and loading spinners. The portal keeps the current slide on screen, prepares the
next one in the background, and swaps only when it is ready — so clicking through a
presentation feels instant, and click-to-advance keeps working even while a slide is loading.

Behind the scenes, page navigation is served from a cache: revisiting a page resolves
immediately instead of asking the server again, and progress indicators appear only when a
navigation is genuinely slow. Slide decks also render correctly from the very first frame —
the slide counter and Previous/Next controls show the right position immediately instead of
briefly reading "Slide 1 / 1".
