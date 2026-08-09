---
Name: Pictures in a pixel-faithful deck export are actually there
Category: Fix
Description: A deck exported with pixel fidelity printed every stored image as a blank space. The export was asking for the picture under the wrong name and could not say so.
Icon: Sparkle
Order: -20260809
---

# Pictures in a pixel-faithful deck export are actually there

Exporting a deck with **pixel fidelity** gave you the slides exactly as they look
on screen — gradients, layout, raw HTML and all — except for one thing: every
picture stored alongside the deck came out as empty space. The text was right,
the backgrounds were right, and where the image belonged there was nothing.

A printed deck is produced offline, deliberately: the print document is not
allowed to reach out to the network for anything, so every picture has to be
carried *inside* it. Collecting those pictures is therefore not a shortcut, it is
the only way one reaches the page. And the step that collected them was looking
in the wrong place.

An image on a slide is addressed by the slide it belongs to. The export read the
first part of that address as the name of the *folder* the picture lives in —
but the first part names the **space**, not the folder. So it asked for a folder
that does not exist, got nothing back, and printed the slide without the
picture. Because the document may not fetch anything, there was no second chance:
the missing image could not quietly load itself the slow way, it was simply
absent.

There is now one shared answer to "where does this file actually live", used both
by the export and by the page you look at in the browser — so the two can no
longer disagree about it. Every way an image can be referenced resolves the same
way in both: a picture stored with the slide, a picture stored in a named folder,
and a folder or file name containing spaces or slashes.

## And when a picture genuinely is missing, the export says so

If a slide points at a file that has been deleted or renamed, the export used to
carry on silently and hand you a deck with a hole in it — indistinguishable from
the bug above. The export log now names every picture it could not find and says
it will print blank, so a missing file looks like a missing file rather than a
rendering fault.
