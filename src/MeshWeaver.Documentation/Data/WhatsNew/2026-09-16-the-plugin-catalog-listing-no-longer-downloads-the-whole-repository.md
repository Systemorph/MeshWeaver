---
Name: Opening the plugin catalog no longer downloads the whole plugins repository
Category: Fix
Description: The catalog listing read two small files per plugin by transferring the entire source repository — 47.8 MB and 13 seconds for 0.8 MB of manifests — which is why opening the catalog or rendering /Store could sit for half a minute and then fail. It now transfers only the manifests it parses.
Icon: CloudArrowDown
Order: -20260916
---

# Opening the plugin catalog no longer downloads the whole plugins repository

Opening the plugin catalog, installing a plugin, or rendering `/Store` asks a registry for its
catalog. That request could take **12 to 19 seconds** to produce its first byte, and about **sixty
times a day** it did not finish inside the client's 30-second budget at all.

It was not a busy registry, and it was not the number of plugins. To build the list, the registry
read **two small files per plugin** — each plugin's root and its manifest — and it obtained them by
transferring the **entire source repository** and then discarding almost all of it.

Measured against the plugins repository on 2026-09-16:

| | files | bytes | time |
|---|---|---|---|
| what was transferred | 4,995 | 47.8 MB | **13.0 s** |
| what was actually read | 143 | 0.8 MB | 0.08 s |
| what is transferred now | 143 | 1.3 MB | **3.3 s** |

The registry now selects the files before it transfers them, rather than after: it fetches the
repository's structure without any file contents, picks out the manifests, and downloads only those.
The answer is identical — the same catalog, at the same commit — and it arrives with room to spare
inside the budget that used to be missed.

## What you will notice

- **The catalog and `/Store` open promptly** instead of occasionally sitting for half a minute and
  then failing.
- **Installs and updates stop stalling on the listing** that precedes them. The failure this removes
  was rarely visible as an error: the request usually succeeded on a later attempt, so what it
  produced was a long wait rather than a message.
- **Nothing changes about what you can see.** Which packages a registry offers you is decided per
  request, from your own installation's grant, exactly as before — the part that is now cheaper is
  reading the repository, which is the same for everybody.

Installing a plugin still reads that plugin's whole folder, and syncing content still transfers
everything, both unchanged: only the *listing* was ever reading two files per plugin out of a whole
repository.
