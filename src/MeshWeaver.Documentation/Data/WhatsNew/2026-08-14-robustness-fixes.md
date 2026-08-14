---
Name: Steadier page loads and web search
Category: Fix
Description: A dead link in a web search no longer aborts the search, and a timing problem that could drop a page on load is gone.
Icon: Sparkle
Order: -20260814
---

# Steadier page loads and web search

Three small things that could go wrong at the edges no longer do.

When an agent searched the web and one of the results pointed at a page that had since been removed,
the whole page fetch failed. Now the missing page is simply reported as missing and the agent carries
on with the other results.

Occasionally a page load could fail outright because the browser measurement the layout depends on
ran before the script that provides it had loaded. The measurement now brings its own script along,
so the order no longer matters.

Finally, a portal that starts up while its plugin registry is briefly unreachable now retries instead
of giving up for the lifetime of that instance — so a momentary network hiccup no longer leaves
installed plugins showing an old version until the next restart.
