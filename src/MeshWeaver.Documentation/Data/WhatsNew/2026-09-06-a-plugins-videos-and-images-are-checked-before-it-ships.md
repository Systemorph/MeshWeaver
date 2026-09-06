---
Name: A plugin's videos and images are checked before it ships
Category: Fix
Description: The pre-publication check now installs a plugin's videos, posters and images the way a portal does and reads every one of them back, so a broken or missing file is caught before release instead of turning up as a blank player on your page.
Icon: Video
Order: -20260906
---

# A plugin's videos and images are checked before it ships

A plugin, a course or an app brings more than text with it. Its **videos, posters, images and
fonts** ship alongside its pages, and installing it is only half-done until those files are actually
being served. The check that runs before any of it is published verified the pages thoroughly — and
never once looked at the files.

It could not: the check ran on a stripped-down environment that had nowhere to put them, so every
plugin's files were quietly turned away, every run, and the check still came back green. A course
whose intro video pointed at a file that never arrived would pass, and the first sign of trouble was
a blank player on the page after it had been installed.

The check now sets up the same file storage a real portal has. Every file a plugin carries is
installed exactly as it would be on your own portal, and then **read back through the very route
your browser uses** — a course's `<video>` tag, an image on a page, a font on a card. If a file did
not arrive, or arrived somewhere your page would never look for it, the plugin is held back and the
report names the file.

The report also says what it verified, not merely that nothing complained: each plugin now shows
`2/2 files served`, so "everything is fine" and "nothing was looked at" can no longer read the same
way.

If you maintain plugins, one thing follows from this: a run that used to end with a wall of warnings
about files that were *"installed but not being served"* now ends without them. Those warnings were
about the check's own environment and never about your plugin — they are gone because the condition
they reported is gone, not because they were turned off.
