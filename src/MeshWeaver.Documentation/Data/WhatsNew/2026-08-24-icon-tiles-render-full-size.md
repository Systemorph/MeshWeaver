---
Name: App icons fill their tiles
Category: Fix
Description: Icons authored with a fixed pixel size no longer render tiny inside larger tiles — the tile size always wins.
Icon: Sparkle
Order: -20260824
---

# App icons fill their tiles

Icons authored with a fixed pixel size used to paint small inside larger surfaces — a 24-pixel
icon in a 64-pixel app tile. The renderer now always draws an icon at the size the surface asks
for, on the web and in the native app alike.
