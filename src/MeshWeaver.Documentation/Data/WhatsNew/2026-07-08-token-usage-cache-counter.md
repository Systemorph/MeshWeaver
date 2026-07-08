---
Name: Token counter now counts cached tokens
Category: What's New
Description: The chat token counter finally accounts for prompt-cache tokens across every provider, with a cached figure and cache-aware cost.
Icon: Sparkle
---

# What's New — 8 July 2026

## Token counter counts cached tokens

The in/out token counter used to drop prompt-cache tokens entirely, so it under-reported the real, billable context on cache-heavy agents. It now captures cache read/write across every provider, surfaces a `⚡ cached` figure in the usage chip and the per-message badge, and prices cache reads/writes correctly. Anthropic prompt caching is also enabled on the static prompt prefix so repeated context bills at the reduced cache-read rate.
