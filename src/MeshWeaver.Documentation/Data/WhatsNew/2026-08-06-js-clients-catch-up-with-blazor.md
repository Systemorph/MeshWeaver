---
Name: The web and mobile clients speak German, and render everything the portal renders
Category: What's New
Description: The Next.js and React Native clients now follow your language setting, the mobile app renders charts, pivot tables, dialogs, video and file browsing, and the chat side panel gained its missing buttons.
Icon: Globe
---

# The web and mobile clients catch up with the portal

The portal ships in English and German, and until now only the main Blazor portal actually
honoured that: the Next.js client and the React Native app showed English to everyone, whatever
language your profile was set to. Both now follow the same setting, from the same translations the
portal uses — so switching your profile to Deutsch changes all three. On the web client your
profile wins over your browser's language, so a German profile stays German on an English machine.
The mobile app, which connects without signing in, follows the device language.

The mobile app also renders a lot more of what the portal renders. Charts, pivot tables, dialogs,
video, tabbed and split layouts, form field labels, the file browser, document export and node
import/export all work now; several of them previously showed a grey placeholder box, and a few
kinds of page silently lost their content entirely. Chat, search, node collections and the
appearance settings are live rather than placeholders. Where a phone genuinely cannot do what a
browser does — playing an embedded YouTube video inline, for instance — it opens in the system
app instead of failing quietly.

In the web client, the chat side panel gained the three buttons it was missing: start a new
thread, pick up a recent one, and move the current conversation into the main view. Previously
only Close was available, so continuing an earlier conversation meant navigating to it by hand.
