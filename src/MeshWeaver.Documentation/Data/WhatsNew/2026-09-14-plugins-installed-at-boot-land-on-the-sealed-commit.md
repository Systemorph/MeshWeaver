---
Name: Plugins installed at boot land on the sealed commit
Category: Fix
Description: A portal's boot-time plugin install now takes the same commit its content sync is held to — the one sealed for the running platform — so the two no longer overwrite each other's files on every restart.
Icon: LockClosed
Order: -20260914
---

# Plugins installed at boot land on the sealed commit

A portal keeps its plugin content in step with the platform it runs: a repository's sources
advance only to the commit that was **sealed** for that platform build, so a portal never receives
source files no build compiled against its own platform. Every content sync honoured that. The
plugin install that runs at boot did not — it fetched the branch tip, whatever the seal said.

On a portal that had both, the two disagreed about which tree a partition should hold. Each restart
the sync put the partition back on the sealed commit and removed the files only the newer tree
had; the boot install then re-applied the tip as a diff against its own record, which listed those
files as unchanged, so they stayed missing. Eight node types of the Hosting plugin sat unable to
compile for that reason.

The boot install now asks the seal first, exactly as the content sync does:

- **Sealed for this platform** — the plugin is listed and installed at that commit, and the install
  record says which commit it came from.
- **Not sealed here** (a repository this portal runs no compiled module of) — it keeps installing
  from the configured branch, as before.
- **Sealed but incomplete** — nothing is installed from that repository this boot. The portal says
  so, and picks it up once the seal is whole; it never falls back to the branch.

The symptom this removes: plugin content that appears, disappears and reappears across restarts,
and node types that fail to compile because sources their record lists are not on the mesh.
