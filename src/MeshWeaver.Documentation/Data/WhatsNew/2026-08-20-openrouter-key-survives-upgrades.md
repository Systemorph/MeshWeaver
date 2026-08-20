---
Name: The OpenRouter key survives deployments
Category: Fix
Description: Deployments can now carry their OpenRouter API key in the release values, so OpenRouter-served models keep working after an upgrade instead of losing their key.
Icon: Sparkle
Order: -20260820
---

# The OpenRouter key survives deployments

Deployments that serve models through OpenRouter had no place to declare the API key in their
release configuration — the key only existed as a live, hand-applied setting, and an upgrade could
ship without it, leaving OpenRouter-served models unable to answer.

The release configuration now carries the OpenRouter key alongside the other AI provider keys, so
it is part of every deployment and models keep working across upgrades.
