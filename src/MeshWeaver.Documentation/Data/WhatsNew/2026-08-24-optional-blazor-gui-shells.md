---
Name: Choose your portal: Blazor, the new Next shell, or both
Category: Feature
Description: A deployment now composes its GUI shells — Blazor-only, Next-only, or both — and on a portal serving both, ?gui=next / ?gui=blazor switches your browser between them.
Icon: Sparkle
Order: -20260824
---

# Choose your portal: Blazor, the new Next shell, or both

Which GUI a portal serves is now deployment configuration. A deployment can run the classic
Blazor portal, the new Next-based portal, or both side by side — and a Next-only portal carries no
Blazor at all: no circuit, no server-side rendering pipeline, just the mesh services the modern
shell talks to.

On a portal serving both — like meshweaver.cloud — add `?gui=next` to any page to switch your
browser to the new shell, and `?gui=blazor` to switch back. The choice sticks per browser; every
page keeps its address across the switch.
