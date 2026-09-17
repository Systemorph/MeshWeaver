---
NodeType: Markdown
Name: "An Answer Nobody Gave Is Not Cached"
Abstract: "A synced query chain is Replay(1) for the life of the process, and MeshQuery counts a provider that completes without an Initial as an EMPTY Initial — so a cold moment used to become a permanent false 'absent' for every later reader. The frame now NAMES the providers that never answered, and a frame nobody answered is delivered but not kept."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#37474f'/><circle cx='12' cy='12' r='6' fill='none' stroke='white' stroke-width='2'/><path d='M12 8v5' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='12' cy='16' r='1' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Queries"
  - "CQRS"
---

# An answer nobody gave is not cached

**"Nobody answered yet" and "there is nothing there" are different facts. Until they were told apart,
a cold moment on a replica made a durably present node read as absent — permanently, for that
process, with no error anywhere.**

## The three properties that compose into it

1. **A synced query chain is the process's answer, once.** `MeshNodeStreamCache.GetQueryRaw` builds
   every `hub.GetQuery` chain as `Replay(1)` + `AutoConnectOwnedBy` and keeps it in a registry that
   is never rebuilt. The FIRST frame is replayed to every later caller. That is the point of the
   cache — one upstream subscription, cheap re-reads — and it is right for a real answer.
2. **An empty frame is an ordinary outcome, even when no provider spoke.**
   `MeshQuery.MergeProviderObservables` counts a provider that COMPLETES WITHOUT an `Initial` as an
   EMPTY Initial, deliberately: the alternative starved the merge's gate and hung every real-user
   unpinned search for 300 s (prod, 2026-07-03). So the merge proceeds — correctly — and hands
   downstream a frame that says "nothing matches" with nothing behind it.
3. **Nothing corrects it.** The only event that refreshes a live chain is a change notification for
   a matching path, and the commonest writer of such a path is a reconcile re-writing an unchanged
   node — a NO-OP at the store (`IsNoOpUpsert`), which publishes nothing.

Cold answer + permanent cache + no corrective event = a false absence that outlives everything short
of a restart.

## Measured

`Admin/_LogIncident/d1cd36f53a5f3a6c` on memex-cloud — the plugin gate, **11,927 occurrences**
(MeshWeaver#1246). All ten retained samples fall in the 24 minutes after the 2026-09-16 21:24Z
restart; the incident went quiet at 21:48Z. Every path they name, read afterwards:

| the gate logged, at Error | the store held |
|---|---|
| `Codex`, `Antigravity`, `Grok`, `OpenCode` — *write did NOT become durable … a provisioned partition lost a write* | all eight `_Access` grants at **version 1**, written 2026-09-13, untouched since |
| `Feedback` — the same line for `Feedback/_Submissions/_Access/*` | both denies at **version 1**, written 2026-08-03, untouched since |
| `Governance`, `BuildServer`, `WhatsApp`, `iMessage` — *reconcile is NOT CONVERGING — rewrote …/_Policy* | each `_Policy` in the exact shape the gate wanted, versions 2–3, **no version written on 09-16 at all** |

Nine of ten are a durably present node read as missing. The burst-after-restart-then-quiet shape is
the signature: the cold read happens while every per-node hub activates, and the wrong answer then
sticks to the chain.

## What the framework does now

- **The frame says what it is worth.** `QueryResultChange.SilentProviders` NAMES the providers that
  completed without an `Initial` and were counted as empty — the sibling of `Partitions`, which
  already exists so a zero can be read against the set it was read from. Null/empty means every
  provider answered.
- **The single-provider path honours the same contract.** A lone provider that completed silently
  used to leave the merged stream completing with NO frame at all, which a caching consumer keeps
  just as durably ("completed, nothing"). It now delivers the empty Initial and names the provider.
- **A frame nobody answered is delivered, not kept.** `MeshNodeStreamCache` drops that chain from
  its registry (`EvictUnansweredQuery`, pair-exact on `(id, query set)` like the faulted-chain
  eviction of #1316), so the next `GetQuery` for that id opens a fresh chain and asks again. Live
  subscribers keep the chain they hold — it is a real, running query whose next frame may well be
  complete — and its upstream is NOT released, because tearing a live read out from under readers to
  save one subscription is a worse trade than one extra subscription.

What it deliberately is **not**:

| not | why |
|---|---|
| an expiry / TTL on cached answers | a timer re-asks a question nobody asked and still caches the cold answer until it fires; the defect is not age, it is provenance |
| a bypass at each call site | every future consumer of `hub.GetQuery` would inherit the exposure again, and the call site cannot see what the merge saw |
| refusing to emit (waiting for a real Initial) | that is the 300 s hang the counted-as-empty rule was introduced for |
| dropping `Replay(1)` | a genuine answer SHOULD be replayed; that is the cache |

Pinned by `ColdEmptyInitialIsNotCachedTest` (real mesh: a provider that is silent on its first
subscription and answers afterwards — the second read must find the node, and the control arm proves
an answered frame is still cached) and by `MeshQueryMergeContractTest` (the merge names silent
providers, names none when all answered, and the single-provider path answers an empty Initial that
names itself).

## The consumers this was reaching

A judgement per single-node / decisive-emptiness consumer of `hub.GetQuery`, swept over both
repositories at 2026-09-17. "Cost" is what a cold false-absent buys; all of them are now served by a
chain that cannot keep an unanswered frame, so the residual exposure is one wrong answer to the caller that
triggered the cold read, never a permanent one.

### `Systemorph/MeshWeaver`

| site | what a cold "absent" cost |
|---|---|
| `PermissionEvaluator` / `SecurityQueries` folds (`$security-access`, `$security-memberships`, `$security-roles`) | the sharpest: no grants / no memberships reads as "denied", and #697 already records a frozen path-less Initial making a group removal invisible until restart |
| `InstanceConsentService` (consent text, consent record, registry credential) | consent re-prompted or read as never given; a registry credential read as absent |
| `InstanceRegistryAuthenticator` (`instance-key-listing:{parent}`) | a live instance key read as unknown → registration refused |
| `GitHubCredentialService`, `GitHubSyncService` (`github-cred`, `gitsync-cfg`) | a stored credential / sync config read as absent → the sync runs unauthenticated or not at all |
| `MeshNodeExtensions` (`UserActivity|{path}`) | an existing activity record read as absent → a duplicate written |
| `NotificationSettingsNodeType`, `HomeConfigNodeType` | the viewer's settings read as unset → defaults rendered, and a save can then overwrite them |
| `EventSubscriptionRunner`, `InstalledPackageRepairService`, `InstanceAutoRegistrationService`, `RegistryUpdateReconciler` | "nothing subscribed / nothing installed" → work skipped or redone |
| `NodeTypeLayoutAreas`, `NodeSources`, `MenuPresentationOverlay` | a listing renders empty and stays empty for the process |
| `NodeTypeCompilationHelpers` (`nodetype-include-arrival`) | low: it waits for an arrival rather than treating empty as final |

### `Systemorph/MeshWeaver.Plugins`

| site | what a cold "absent" cost |
|---|---|
| `MeshQueries.FindNode` (≈48 call sites: the plugin gate, the installer, coupons, covers) | the measured one — MeshWeaver.Plugins#1994 additionally moved it to a listing for EXISTENCE + the owner for CONTENT, so its negatives are no longer taken from a cache at all |
| `AiSettingsNodeType` (three sites) | a user's AI settings read as unset |
| `ModelCreditGate` / `ModelCreditLedger` (tier, grace) | a credit tier or grace record read as absent → the wrong gate decision |
| `OperationRequestControlPlane` (prelude, activity) | an operation's own records read as missing |
| `Store` (`Subscription.Live`, `Keys.Live`), `Edu` (`LearningJourney.Installed`) | a live view renders as "not subscribed" / "not installed" |
| `InstanceSyncService`, `DeviceSeed` | a sync config or seed grant read as absent → re-seeded or skipped |

## Related

- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a single known
  node's content comes from its owner, and a query is for sets.
- [Silent Completion](/Doc/Architecture/SilentCompletion) — the sibling shape: an empty completion is
  invisible to every timeout.
- [Query Identity](/Doc/Architecture/QueryIdentity) — the other way a read answers "absent" without
  anything being absent.
