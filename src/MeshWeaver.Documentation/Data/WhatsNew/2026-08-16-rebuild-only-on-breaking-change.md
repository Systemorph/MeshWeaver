---
Name: Updates recompile content only when the API actually changed
Category: Feature
Description: The platform now keys compiled content to the framework's API surface, so routine internal updates keep every compiled node type warm instead of rebuilding them all.
Icon: Sparkle
Order: -20260816
---

# Updates recompile content only when the API actually changed

Until now, every platform update invalidated all compiled dynamic content — even an internal-only
fix that changed no API — so each roll re-did work whose inputs had not changed. Compiled content
is now keyed to the framework's API surface: an update that does not change what content compiles
against keeps every existing build valid, on the server and in the published CI bake alike.

What you notice: most platform updates now roll through with content staying warm end to end —
no re-compilation wave, no cold first visit after a routine update. When an update genuinely
changes the API, content is rebuilt once on CI and reused everywhere, as before.
