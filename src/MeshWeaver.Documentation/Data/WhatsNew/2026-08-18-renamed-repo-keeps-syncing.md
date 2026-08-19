---
Name: A renamed GitHub repository keeps syncing
Category: Fix
Description: Spaces synced from a repository that was renamed on GitHub keep updating, and a webhook that matches no Space now says so loudly.
Icon: Sparkle
Order: -20260818
---

# A renamed GitHub repository keeps syncing

Renaming a repository on GitHub used to quietly stop every Space that synced it from updating. GitHub redirects the old address, so nothing appeared broken — pulls, pushes and manual syncs all kept working — but the automatic update after each green build compared the stored address against the new name and never matched. Spaces went on serving whatever content they last imported, with no error anywhere.

Those Spaces now keep updating: when the stored address matches nothing, the repository's current name is looked up and used instead, and the Space's GitHub settings are corrected in place so the stored address stops showing a name the repository no longer has.

A delivery that genuinely matches no Space is also no longer silent. It is reported as a warning naming both the repository the update came from and the repositories it was compared against, so a misconfigured or misdirected webhook is visible the first time it happens instead of after days of stale content.
