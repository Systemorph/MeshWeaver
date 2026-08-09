---
Name: Documentation page embeds now render
Category: Fix
Description: Diagrams and inline examples embedded in documentation pages now display instead of "not found"
Icon: Sparkle
Order: -20260716
---

# Documentation page embeds now render

Documentation pages that embed a shipped asset — an architecture diagram, an inline
markdown example, an image — now display it inline instead of showing an "Area not found"
placeholder. The assets a page ships alongside it are served directly, so every doc page
renders exactly as authored, on a fresh deployment and offline.
