---
Name: A new plugin lands with its own pages
Category: Fix
Description: A freshly installed plugin could open on a bare, generic page — none of its own views — until something recycled it. Its front page is now built only once the plugin's code is ready to serve it.
Icon: AppsAddIn
Order: -20260810
---

# A new plugin lands with its own pages

Installing a plugin sometimes ended with an oddly empty result. The install
reported success, every node was there, and the plugin's front page opened — but
it was the generic node page, not the plugin's own. The Store's catalog, a
course's landing view, a shop front: gone, replaced by the standard tabs every
node has. Opening a page *inside* the plugin worked fine. Only the front page was
wrong, and it stayed wrong until someone recycled it or the portal restarted.

The cause was a matter of a few milliseconds.

A plugin ships its own code, and that code is compiled here, on your portal,
against the exact version of the platform you are running. A plugin repository
also carries the result of the *last* compile someone did — which was against a
different version, months ago, on a different machine. So the first thing your
portal does with a newly installed plugin is rebuild it.

Meanwhile the installer opens the plugin's front page once, on purpose, so the
plugin is ready the moment you click on it. A page works out which code serves it
exactly once, when it first opens, and then keeps that answer. Opening the front
page while the rebuild had not started yet meant the page asked the question too
early, got "no usable code here", and kept that answer for good — while the
rebuild finished a few seconds later and changed nothing, because nobody asked
again.

Now the installer checks first. If the plugin's own code has not been rebuilt yet,
it simply does not open the front page — because opening it is only a
head start, while opening it too early is a wrong answer that sticks. The page is
then built the first time someone actually visits, by which point the rebuild has
long finished, and it gets the right answer the only time it asks. Installs never
wait on a compile, so nothing gets slower.

You should notice nothing except that a newly installed plugin now looks the way
its author intended, first time.
