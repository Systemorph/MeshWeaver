---
Name: GitHub App Credentials
Category: Architecture
Description: Which GitHub App mints which token in core CI — meshweaver-cloud writes to the repository a workflow runs in, the read-only fleet-reader performs every cross-repo read — why a read on the wrong App either freezes main or silently unlocks a live portal's image, and the order that moves a repository between installations safely.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="4.5"/><path d="M10.7 12.3 21 2"/><path d="m16 7 3 3"/><path d="m19 4 2 2"/></svg>
---

# GitHub App Credentials

Core CI mints every GitHub credential per run from a GitHub App installation
(`actions/create-github-app-token`), never from a stored PAT: a PAT has no owner, no expiry anyone
watches, and fails indistinguishably from a scope problem (#1709). **There are two Apps, split by
what the token may do.**

| App | Grants | Installed on | Mints for | Secrets |
|---|---|---|---|---|
| `meshweaver-cloud` | Contents, Pull requests: **write**; Metadata: read | the org (measured 2026-09-11: `repository_selection: all`, 35 repositories); Memex is leaving it | WRITES to the repository the workflow runs in — `auto-arm`, `arm-credential`, `merge-queue-steward`, `release` (the next-line PR), the satellites' `node-repo-platform-ref-bump` | `MESHWEAVER_APP_ID`, `MESHWEAVER_APP_PRIVATE_KEY` |
| `fleet-reader` | Contents, Metadata, Pull requests: **read only** | the eight fleet repositories — MeshWeaver, MeshWeaver.Plugins, Memex, MeshWeaver.Education, MeshWeaver.Reinsurance, MeshWeaver.SocialMedia, MeshWeaver.Manufacturing, MeshWeaver.Crm | every cross-repo READ (next table) | `FLEET_READER_APP_ID`, `FLEET_READER_APP_PRIVATE_KEY`, in **both** secret stores |

The rule that makes this auditable from the workflow files alone: **a `create-github-app-token`
step that requests any `permission-*: write` uses `meshweaver-cloud` and names only
`${{ github.event.repository.name }}`; every other step uses `fleet-reader` and requests `read`
explicitly.**

## The reads `fleet-reader` serves

| Workflow · job | Reads | Requests | On an App that does not reach a repository |
|---|---|---|---|
| `dotnet-test.yml` · `shared-rules` | `AGENTS.md` in every repo of `.github/shared-rules.json` | contents | **red at the mint** — and the job is a `needs:` of the required `Consolidate test results` |
| `dotnet-test.yml` · `cross-repo-pair` | the pull request a `Pairs-with:` declares | contents, pull-requests | **red at the mint**, same required chain |
| `shared-rules.yml` | the same as `shared-rules`, on a clock | contents | red at the mint |
| `chart-drift.yml` · three jobs | Memex's per-environment overlays | contents, `repositories: Memex` | red at the mint |
| `pinned-digests.yml` · two jobs | every repository the installation reaches (discovery) | contents | **silent** — the repository drops out of the denominator |
| `lock-pinned-digests.yml` · two jobs | the same, plus the deployment overlays | contents | **silent** — see below |
| `combo-verify.yml` · `verify` | the module source repositories | contents | the source fetch fails |

## Why: one loud failure and one silent one

**The loud one freezes main.** A mint that names a repository the installation does not reach
fails. `shared-rules` and `cross-repo-pair` name Memex and are `needs:` of the required check with
explicit fail steps, so the day Memex left `meshweaver-cloud`'s installation, every pull request and
every merge-queue group would have gone red — main frozen by a credential change no diff made.

**The silent one unlocks a live portal's image.** The pinned-digest sweep and the lock *discover*
their fleet from `/installation/repositories`, so an App that stops reaching a repository does not
fail — it drops the repository and reports on the rest. Measured 2026-09-11 (run 34549778498), the
lock's overlay axis found tag pins in two repositories, and all but one of the tags it listed came
from Memex's deployment overlays: the images the live portals run. On an App that no longer reaches
Memex those tags stop being locked, and the nightly purge may delete an image a running portal needs
on its next pod start. Nothing goes red.

The same measurement priced the narrower installation: `meshweaver-cloud` reached 35 repositories,
and the 27 outside the fleet declared **zero** digest pins and zero platform pins (run
34566118139). An eight-repository `fleet-reader` loses nothing the sweep has ever found — and
installing `fleet-reader` is now what adds a repository to the sweep.

## Why every read moved, not only the ones that name Memex

- **One rule instead of a list.** No read depends on `meshweaver-cloud`'s installation any more, so
  the next repository to leave it cannot re-freeze main.
- **A read-only App cannot write**, whatever a step requests. The explicit `permission-*: read` on
  each mint is belt and braces: it keeps a later-widened App from widening the token.
- **The discovery lanes' denominator becomes the fleet by definition**, instead of whatever else
  happens to be in the org.

## Why `fleet-reader` needs Pull requests: read

`cross-repo-pair` resolves the counterpart a `Pairs-with:` names through
`GET /repos/{repo}/pulls/{n}`. On a private sibling that needs Pull requests: read; Contents alone
answers 404, which the gate reads as "the App is not installed". The mint requests the permission
explicitly, so an App created without it fails that mint RED naming the permission — never a silent
downgrade.

## Provisioning: both secret stores

`FLEET_READER_APP_ID` and `FLEET_READER_APP_PRIVATE_KEY` are consumed by `pull_request` lanes
(`shared-rules`, `cross-repo-pair`), so they go in **both** the Actions and the Dependabot store —
see [The Dependabot Secret Store](../DependabotSecretStore). Those two gates carry a
`dependabot[bot]` actor exemption today; core provisions both stores anyway, because the exemption is
a property of today's `if:` and the store is a property of the event.

```bash
gh secret set FLEET_READER_APP_ID          --repo Systemorph/MeshWeaver                  --body "<app id>"
gh secret set FLEET_READER_APP_ID          --repo Systemorph/MeshWeaver --app dependabot --body "<app id>"
gh secret set FLEET_READER_APP_PRIVATE_KEY --repo Systemorph/MeshWeaver                  < fleet-reader.pem
gh secret set FLEET_READER_APP_PRIVATE_KEY --repo Systemorph/MeshWeaver --app dependabot < fleet-reader.pem
```

Prove the pairing before trusting it: mint an RS256 App JWT with the PEM and call
`GET https://api.github.com/app` — the answer's slug must be the fleet-reader App. Never print the
PEM; pipe it from the file.

## Moving a repository between installations: the order

1. Create the App (read-only Contents, Metadata, Pull requests) and install it on every fleet
   repository.
2. Set both secrets in both stores.
3. Merge the change that moves the mints. Merged before step 2, every core pull request goes red at
   the credential assertion — by design: that is the gate naming what to provision.
4. Prove it: a pull request's `shared-rules` and `cross-repo-pair` are green; dispatch
   `chart-drift`, `pinned-digests`, `lock-pinned-digests` and `shared-rules.yml`, and read each
   denominator — the lock must still list Memex's overlay pins.
5. Only then narrow `meshweaver-cloud`'s installation.
6. Prove it again after narrowing: `shared-rules`, `cross-repo-pair` and a scheduled `chart-drift`
   run are green.

## Related

- [Shared Rule Blocks](../SharedRuleBlocks) — the gate `shared-rules` runs
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the gate that needs Pull requests: read
- [The Dependabot Secret Store](../DependabotSecretStore) — why "both stores"
