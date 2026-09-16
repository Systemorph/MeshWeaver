---
Name: A sync that refuses every pass now says so on its node
Category: Fix
Description: A GitSync source whose configured subdirectory matches nothing in the repository refuses every pass — correctly, because importing an empty snapshot would prune the whole Space. Until now it wrote nothing to the sync config, so a Space that had never once synced looked exactly like one that was up to date.
Icon: PlugDisconnected
Order: -20260916
---

# A sync that refuses every pass now says so on its node

A Space synced from a repository subdirectory will refuse to import when that subdirectory matches
nothing at the commit being synced. The refusal is deliberate: an empty snapshot would be read as
"the repository carries nothing", and the import would mirror the entire Space away.

What was missing is that the refusal left no trace anywhere an operator looks. It wrote a warning to
the log and nothing to the sync configuration — so a source that had refused on **every pass since
it was created** presented exactly like one that was working, and because the refusal is by design
it could never resolve on its own.

The config now records it:

```
lastSyncOutcome: Refused
lastSyncNote:    No files found under subdirectory 'DeepSign' at 061976bc in … Refusing to
                 import an empty snapshot — it would prune the whole Space. Check the
                 subdirectory (including its exact capitalisation — git paths are case-sensitive).
```

`Refused` is deliberately distinct from `Held`. A held source is waiting for a publication it will
eventually receive; a refused one has a configuration fault that repeats forever and clears only
when someone edits the source. The note names the subdirectory, because that is the half that has to
change.

The import still fails exactly as it did — the refusal is recorded and then re-thrown — so nothing
that relied on the error behaves differently.

Full detail: [Publication Seal Starvation](/Doc/Architecture/PublicationSealStarvation).
