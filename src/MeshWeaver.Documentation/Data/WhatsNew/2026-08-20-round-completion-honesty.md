---
Name: A chat round can no longer claim success it did not have
Category: Fix
Description: A round that lost a tool call, or whose model never wrote a final answer, now ends with an error that says so instead of a green "Completed".
Icon: Sparkle
Order: -20260820
---

# A chat round can no longer claim success it did not have

Occasionally an agent would finish a turn with a confident summary — "Confirmed, the entry is saved" — while the tool call that was supposed to do the work never actually ran. The turn still showed as completed, so there was nothing on screen to tell you the work had not happened.

A turn now only reports success when it really produced what success means: every tool it started came back, and the assistant actually wrote a closing answer. If a tool call is left hanging, or the model goes silent instead of finishing its reply, the turn ends with an error that names what went wrong and invites you to re-run it — the partial text is kept, so you can see how far it got.

Tool calls that fail are also recorded as failures now. They previously showed a green tick in the tool list even when the underlying operation had been rejected, which hid real problems from both the chat view and monitoring.
