---
Name: A change to a module's code no longer reinstalls its whole package
Category: Fix
Description: When an update changed only the compiled code of a package that ships a module — Education's course services, or the AI engine that rides along with several packages — the portal asked for a code file that is only ever delivered inside the module, logged an error, and reinstalled the whole package, recompiling every type in it. The update now leaves code to the module and writes only the content that changed.
Icon: PlugConnected
Order: -20260915
---

# A change to a module's code no longer reinstalls its whole package

Some packages ship two things: **content** (pages, types, their source nodes) and a compiled
**module** built from C# code kept next to them. Each package's lock file lists both, so that a
change to the code alone still counts as a new version of the package.

## What went wrong

An update compares the new lock with the one that was installed and fetches only the files that
changed. It fetched the changed **code** files too — but the source that serves a package's content
never serves them, because code only ever travels compiled, inside the module. So the fetch came
back short, the portal logged an error such as

> Updating Edu incrementally: the source returned 0 of the 1 file(s) asked for —
> [src/MeshWeaver.Courses/CourseAssetService.cs] did NOT travel

and fell back to reinstalling the whole package, recompiling every type in it. That happened on
every update that touched a package's code — including every change to the AI engine, whose code
rides along with several packages.

## What happens now

The update leaves code files to the module, which already delivers them, and fetches only the
content that changed. The installed record still lists the code files at their new version, so the
next update does not ask about them again. A file the update *does* ask for and does not receive is
still treated as a real problem and still triggers a full reinstall.

The mechanism is written up in
[Declared Is Not Landed](/Doc/Architecture/DeclaredIsNotLanded) → "…and the lock's module sources
were exactly such paths".
