---
Name: Embedded sections no longer show comment controls
Category: What's New
Description: "@@ embeds render clean content — comment highlights, the selection Comment button, and the Add Comment footer now appear only on a node's own page."
Icon: Sparkle
---

# Embedded sections no longer show comment controls

Pages composed of `@@` embeds used to repeat the commenting surface for every embedded
section: each one showed its own "Add Comment" footer, offered a Comment button on any
text selection, and displayed comment highlights from the embedded node. On a proposal
or course page built from several sections, that read as comments everywhere.

Embeds now render the content only. Commenting still works exactly as before on each
node's own page — open the embedded section directly to read or add comments. Authors
who want an embed to keep its full page chrome, including comments, can opt back in
with `@@node?showHeader=true`.
