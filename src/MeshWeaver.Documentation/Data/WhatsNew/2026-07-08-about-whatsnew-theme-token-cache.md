---
Name: About page, What's New, top-bar theme toggle & cache-aware token counter
Category: What's New
Description: An About page that pins the exact running build, a What's New feed, a light/dark toggle moved into the top bar, and a token counter that finally counts prompt-cache tokens.
Icon: Sparkle
---

# What's New — 8 July 2026

## About page

Settings now has an **About** tab that shows exactly which build you're running — platform version plus the git commit it was built from — with a link straight to that commit on GitHub. No more guessing which image a portal is on.

## What's New feed

This page. Each shipped change adds a dated entry under `Doc/WhatsNew`, newest first — produced automatically when a pull request merges.

## Light/dark toggle in the top bar

The theme switch moved out of the settings dialog into the top bar, matching the mobile app: one click flips between light and dark.

## Token counter counts cached tokens

The in/out token counter used to drop prompt-cache tokens entirely, so it under-reported the real, billable context on cache-heavy agents. It now captures cache read/write across every provider, surfaces a `⚡ cached` figure in the usage chip and per-message badge, and prices cache reads/writes correctly. Anthropic prompt caching is also enabled on the static prompt prefix.
