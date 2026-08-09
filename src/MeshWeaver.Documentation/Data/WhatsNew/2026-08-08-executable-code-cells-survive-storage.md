---
Name: Executable code cells keep their Run button on file-backed meshes
Category: Fix
Description: Executable Code nodes no longer lose their executable flag and execution history when persisted to file, blob or git storage.
Icon: Sparkle
Order: -20260808
---

# Executable code cells keep their Run button on file-backed meshes

Code cells marked executable used to silently lose that setting — together with their
execution history and activity links — whenever the mesh stored them on a file system,
in blob storage, or synced them to a git repository. After a restart or re-import the
Run button was gone and running the script was refused.

Such cells are now stored in a lossless format, so the Run button, the last-run
information and the activity trail all survive persistence and sync. Plain C# source
files are unaffected and keep their readable `.cs` form in synced repositories.
