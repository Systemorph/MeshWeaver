---
Name: Editors show their text on mobile
Category: Fix
Description: Code and Markdown editors could render as an empty box in the mobile app; they now show their content.
Icon: Sparkle
Order: -20260814
---

# Editors show their text on mobile

In the mobile app, a code or Markdown editor opened from the server could appear completely empty —
a blank box where the page's text should be, with nothing to suggest anything had gone wrong. The
text was there all along; the app was looking for it in the wrong place.

The same fix covers the search box, and applies to every control of that kind, so a value sent by the
server is displayed wherever it appears.

Separately, an app built on the Node client library could silently lose earlier messages in a
conversation when sending a new one. That library was misreading the live updates it receives, so it
never saw the conversation's existing contents. It reads them correctly now.
