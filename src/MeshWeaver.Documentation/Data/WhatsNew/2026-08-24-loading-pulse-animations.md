---
Name: Loading pulses actually pulse now
Category: Fix
Description: Five loading and progress animations — the chat message skeletons, the delegation and sub-thread pulses, and the area loading dots — were silently static because their keyframes never parsed; they animate again.
Icon: Sparkle
Order: -20260824
---

# Loading pulses actually pulse now

Several "something is happening" indicators were quietly frozen: the skeleton placeholders while
a chat message loads, the pulse on a delegated call and on a running sub-thread, and the three
blinking dots while a layout area loads. Their animations referred to keyframes written with a
Razor escape (`@@keyframes`) inside plain CSS files, where that escape is not processed — so the
keyframes never parsed and the elements just sat still.

The keyframes are plain CSS again, and every one of those indicators moves — so a loading page
looks like a loading page instead of a stuck one.
