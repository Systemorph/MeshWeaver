---
Name: Static assets load reliably
Category: Fix
Description: Media and images served from a space no longer intermittently fail to load, and HEAD requests for them now succeed.
Icon: CheckmarkCircle
Order: -20260806
---

# Static assets load reliably

An image or video served from a space's files could intermittently fail to load on a busy portal — a cover or poster showing up on one page load and blank on the next. That intermittency is gone: these requests now resolve under the visitor's identity (anonymous for public content, so public assets still serve), which is what they needed to succeed consistently.

A `HEAD` request for such a file — the lightweight request a browser or link checker makes to read a file's size and type without downloading it — now returns success instead of being rejected.
