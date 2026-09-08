---
Name: Platform Script Resolution
Category: Architecture
Description: A node repo runs the platform's centralized gate scripts, never a copy — and the loader that lets a developer run the same script locally is NOT portable between repos, because the ref is resolved per script from the lane that runs it. Why a copied loader refuses on every call, and what a vendored script costs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m18 16 4-4-4-4"/><path d="m6 8-4 4 4 4"/><path d="m14.5 4-5 16"/></svg>
---

# Platform Script Resolution

**A node repo does not own its gate scripts.** The reusable lanes fetch the platform's
`.github/scripts/*` at a ref and run them against the caller's tree; the repo keeps only its
*policy* — `scripts/compile-check.allow`, `scripts/gen-manifests.config.json`. That rule is stated in
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture); this page is about the part
that bites afterwards — **which ref, and who resolves it.**

## What the lane actually does

`node-repo-compile-check.yml` fetches the script at the caller's `platform-ref` input and runs it
with the caller's tree and allow-file:

```bash
gh api "repos/Systemorph/MeshWeaver/contents/.github/scripts/compile-check.py?ref=${PLATFORM_REF}" \
  --jq .content | base64 -d > "$RUNNER_TEMP/compile-check.py"
python3 "$RUNNER_TEMP/compile-check.py" --refs ../refs
  # MW_REPO_ROOT:  ${{ github.workspace }}/repo
  # MW_ALLOW_FILE: ${{ github.workspace }}/repo/scripts/compile-check.allow
```

and the input's own description states the contract:

```text
platform-ref:
  description: The Systemorph/MeshWeaver ref whose .github/scripts this gate runs ...
    Default main; pin it to the same sha as the `uses:` line for a reproducible run.
  required: false
```

**A call that omits `platform-ref` runs core `main` — a floating ref.** Two runs of identical code
can execute different guards, and nothing in either run says so.
[Pin Set Consistency](/Doc/Architecture/PinSetConsistency) explains why the pin gate cannot see this:
invariant I3 compares *literals*, so a call passing no `platform-ref` has nothing to compare and the
repository is reported consistent.

## The local runner, and the rule that makes it correct

A developer still needs to run the gate before pushing. The runner is `scripts/platform-script.py`,
and its single job is to **resolve the ref the lane would resolve** and apply the lane's env, so that
a local verdict *is* CI's verdict:

```bash
python3 scripts/platform-script.py compile-check.py          # run it against this repo
python3 scripts/platform-script.py --path compile-check.py   # where the cached file is
python3 scripts/platform-script.py --ref  compile-check.py   # which ref it resolved to
```

The resolution rule is one line, and it mirrors the lane rather than restating it:

```text
<lane>.with.platform-ref    if the call passes one
"main"                      otherwise — the lane's own documented default
```

Two consequences worth stating out loud:

- **The ref is per SCRIPT, not per repo.** The ref that decides which `compile-check.py` runs is the
  compile-check lane's; the ref that decides which `check-workflow-timeouts.py` runs is the validate
  lane's. A repo whose lanes sit at different shas resolves different scripts at different refs, and
  that is correct.
- **A `main` result must not be cached across runs.** `main` moves, so a cache keyed by it is a
  stale-guard generator — exactly the failure the loader exists to end. Cache only an immutable
  40-hex ref.

## 🚨 The loader is NOT portable between repos

This is the trap, because the migration looks like a copy-paste job and is not.

`MeshWeaver.Reinsurance`'s loader resolves **one** sha and refuses unless *every*
`node-repo-*.yml@<sha>` in `ci.yml` agrees:

```python
shas = sorted(set(USES.findall(CI.read_text())))
if len(shas) != 1:
    raise SystemExit("✗ ci.yml pins N different node-repo lane sha(s) — the lanes are ONE contract")
```

That assumption holds in Reinsurance and **fails in `MeshWeaver.Manufacturing`, which pins four lane
shas — and is supposed to.** `check-pin-set-consistency.py` reads that as *consistent*, because I3
pairs a lane call with its **own** `platform-ref` and never with another lane's:

```text
pin-set consistency: 1 repository(ies) examined, 1 consistent, 0 not.
```

So dropping Reinsurance's file into Manufacturing yields a tool that **refuses on every
invocation** — a local runner nobody can run, which sends the next person straight back to a
vendored copy. Manufacturing's loader therefore resolves per script, from the lane that runs it.

**Measure the target repo's lane shas before adopting, and pick the shape that matches.** Neither
loader is "the" loader.

## Why a vendored copy is worse than no copy

A local `scripts/compile-check.py` is never what CI runs, so it can only ever be a **second
opinion** — and a second opinion that drifts is the worst shape a gate can have: it is confidently
wrong, and it disagrees with the check that actually blocks the PR.

Measured across the fleet on 2026-09-08, one file forked three ways, none of them what CI ran:

| repo | vendored `scripts/compile-check.py` | `scripts/platform-script.py` |
|---|---|---|
| MeshWeaver.Crm | 40,610 B | absent |
| MeshWeaver.SocialMedia | 39,515 B | absent |
| MeshWeaver.Manufacturing | removed | present (per-script resolution) |
| MeshWeaver.Reinsurance | removed | present (single-sha resolution) |
| **core `main` — what CI actually runs** | **56,145 B** | — |

**Deleting the copy is not the whole fix.** `README.md`, `AGENTS.md` and
`.claude/skills/gates/SKILL.md` in these repos document `python3 scripts/compile-check.py` as a hard
gate. A bare delete leaves three lying instructions and no way to run the gate at all — which is how
the copy comes back. Land the loader and repoint the docs in the same change.

## Adopting it in a repo

1. **Read the repo's lane shas** out of `ci.yml`. One sha across all lanes, or several? That decides
   which resolution shape the loader needs.
2. **Add `scripts/platform-script.py`** with a `LANE_FOR` mapping from script name to the lane whose
   `platform-ref` decides its ref. Refuse loudly on an unknown script rather than guessing — a guess
   reintroduces the local-verdict-disagrees-with-CI bug the loader removes.
3. **Gitignore the cache** (`.platform-scripts/`) **and add it to every package enumerator's `SKIP`
   set.** `check-skip-sets.py` asserts those sets are equal, so they move together; the count going
   up by one is the positive signal that the edit landed.
4. **Delete `scripts/compile-check.py`, keep `scripts/compile-check.allow`.** The allow file is the
   repo's own policy and the one piece that belongs there.
5. **Repoint the docs** that name the deleted path.
6. **Verify by running it**, not by reading it: `platform-script.py compile-check.py --self-test`,
   plus `--ref` on a script from a lane that *does* pass `platform-ref` and one from a lane that does
   not, so both branches are exercised.

## Related

- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the one build shape, and
  the centralization rule this page implements.
- [Pin Set Consistency](/Doc/Architecture/PinSetConsistency) — why a lane call that omits
  `platform-ref` is invisible to the pin gate.
- [Keeping the Platform Source Pin Current](/Doc/Architecture/PlatformRefBumpLane) — `MW_PLATFORM_REF`
  is the commit a repo's `src/` compiles against; it is a *different* object from a lane's script ref.
- [Module Versioning](/Doc/Architecture/ModuleVersioning) — what the build derives versus what you author.
