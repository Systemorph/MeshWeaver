---
Name: A plugin that syncs from a repository has one writer
Category: Fix
Description: When a space gets its content from a connected repository, the plugin auto-update no longer writes into it as well — it tells you the update is waiting instead, so the space can never end up holding half of one release and half of another.
Icon: ArrowSyncCheckmark
Order: -20260915
---

# A plugin that syncs from a repository has one writer

Some spaces are filled two ways at once: a connected repository keeps them in step with the version
of the code this portal actually runs, and the plugin catalog also installs a package into them and
keeps that package up to date. Both are useful; both were writing, and neither knew about the other.

Each kept its own note of what it had put there. So after the repository sync had refreshed the
space, the plugin update still worked from *its* note — and quietly skipped every file it believed it
had already written. The space ended up holding some files from one release and some from another,
which is a combination nothing was ever built or tested as. On the portal where this was found, a
page stopped rendering with a compile error naming a label that only the newer half of the files
knew about.

**A space now has one writer.** Where a connected repository keeps a space current:

- The plugin's automatic update does **not** write into it. It raises the ordinary "update available"
  reminder instead, saying why it was held — so a package that will not update itself here says so
  once, rather than looking as though nothing has changed.
- The package's compiled part is held with it, so its code can never run ahead of the content the
  repository is keeping — a package still arrives whole, or not at all.
- Anything that still installs there deliberately — your own **Update** on the package card, or the
  portal's own start-up install — installs the package **in full** rather than working out a
  difference from a note that the other writer has made out of date.

Everything else is unchanged: on a space nothing syncs, the installer is the only writer, its notes
are correct, and updates stay as quick and as small as they were.

The symptom this removes: a space whose pages suddenly fail to render, or a plugin type that will not
compile, complaining about something a neighbouring file plainly defines — because the two files came
from different releases.
