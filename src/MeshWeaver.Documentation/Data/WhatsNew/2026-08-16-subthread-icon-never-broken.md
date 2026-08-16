---
Name: Sub-thread icons no longer render broken
Category: Fix
Description: A delegated sub-thread now shows its own avatar — or the standard chat icon — instead of a broken-image placeholder.
Icon: Chat
Order: -20260816
---

# Sub-thread icons no longer render broken

Every thread gets its own generated avatar, and in the chat that avatar was being shown the wrong
way: the sub-thread chip inside a message and the "Running sub-threads" card below the conversation
both drew the browser's broken-image placeholder instead of the picture.

They now show the thread's real avatar, and any icon the portal cannot draw — an unknown icon name,
an empty one — falls back to the standard chat icon rather than a broken image. The same fix applies
to the model and agent pickers in the chat composer, where icons of that kind used to be dropped
silently.
