---
Name: Shipped content no longer recompiles on every start
Category: Fix
Description: The platform ships its own pre-compiled content again, so pages are ready shortly after a portal starts instead of after a minute of warm-up.
Icon: Sparkle
Order: -20260817
---

# Shipped content no longer recompiles on every start

Everything the platform ships as content — the documentation pages, the sample spaces and their
types — is compiled once during the release build and delivered ready-to-run. A portal is supposed
to load that result and start serving straight away.

Since the last release it was silently recompiling all of it instead, on every single start, because
the pre-compiled result was published in a form the running portal could not recognise as its own.
The visible cost was a minute of warm-up after every restart and every update, during which pages
were slow to open.

The release build now produces that result with the very same build the portal runs, so a starting
portal recognises and loads it. Nothing about your content changes — it simply comes up quickly
again.
