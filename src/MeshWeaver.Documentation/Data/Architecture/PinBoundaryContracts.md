---
Name: Pin-Boundary Contracts
Description: "A satellite compiles against a MOVABLE platform pin. Any test or runtime behaviour that encodes a core contract is therefore correct on only one side of that pin — and the pin moves in both directions. Assert the property, or detect which platform you were built against."
---

# Pin-boundary contracts — the pin moves BOTH ways

Every satellite repo compiles its `src/` against a platform commit named by `MW_PLATFORM_REF`. That
pin is **deliberately movable**: forward, because a staleness bound forces it (Plugins#1252, #1264);
backward, because freezing it is a supported response to a bisect or an upstream outage.

So a satellite test — or a runtime behaviour — that encodes a core contract which **changed** is
correct on exactly one side of that pin, and wrong on the other. It does not fail when someone edits
it. It fails when someone edits *the pin*, on a PR whose diff cannot reach the subsystem, and it
looks like that PR's fault.

On 2026-09-03 this class produced **three** incidents in one day, two in tests and one in
production.

## What it looks like

MeshWeaver#3202 anchored the sign-in reads: `LoadUserRoles` stopped asking one unanchored question
over every partition and began asking three anchored ones (root `_Access`, `Admin`, the user's own
partition), and `AccessSubjectQueries.Groups(root)` began declaring its fan-out as `partitions:all`.

Three consequences followed, all from the same boundary:

| where | what happened |
|---|---|
| `MeshWeaver.Plugins` tests | Two suites asserted the PRE-#3202 literals. `main` was red the moment the pin resolved core's tip. |
| the same tests, "fixed" | Replacing them with the POST-#3202 literals failed in the **opposite** direction, because the pin had meanwhile been set to `e7f1d699e`, which predates #3202. |
| production | Image `ci.7658` paired core `e7f1d699` (pre-#3202) with Plugins `12500c9` (post-#1263 refusal). `LoadUserRoles` faulted with `UnanchoredQueryException` and every signed-in request got a **503**. |

🚨 **Both literals were wrong.** That is the diagnostic signature of this class: a fix that makes the
red go away, and then produces the mirror-image red the next time the pin moves.

## The rule

### 1. Assert the PROPERTY, not the literal

A test that pins a core contract should assert what is true on both sides, plus the regression
actually worth guarding.

```csharp
// The ROOT scope's picker is mesh-wide, and says so once the platform requires it.
query.Should().StartWith("nodeType:Group");
query.Should().NotContain("namespace:");
query.Trim().Should().BeOneOf("nodeType:Group", "nodeType:Group partitions:all");

// …and the thing a regression would actually break:
foreach (var scoped in new[] { "ACME", "rsalzmann/Games/Lolo" })
    AccessSubjectQueries.Groups(scoped).Should().NotContain("partitions:all");
```

The last assertion is the point. Fan-out leaking onto a *scoped* leg is what would silently restore
the 199-schema union; the exact spelling of the root leg is not.

### 2. Where the contract itself changed, DETECT which platform you were built against

Assert the matching arm, and **print which one it took** so neither outcome can be mistaken for the
other:

```csharp
var anchored = typeof(SecurityQueries)
    .GetMethod("RootAssignmentsFor", new[] { typeof(string) }) is not null;
Output.WriteLine(anchored
    ? "platform HAS #3202 — asserting the anchored contract"
    : "platform PREDATES #3202 — asserting the unanchored contract");
```

🚨 **By reflection, not by referencing the member.** A direct reference fails to *compile* against
the older pin — which is the same trunk red one error message earlier.

Both arms must be *run*, not argued. Check core out at the pinned sha and build against it:

```bash
git -C ~/code/MeshWeaver worktree add .worktrees/pin-<sha> <sha> --detach
dotnet test <suite> -c Release -p:MeshWeaverRoot=$HOME/code/MeshWeaver/.worktrees/pin-<sha>/
```

### 3. A refusal that can refuse an existing caller lands with its caller sweep, or in GRACE mode

The production 503 was not a test problem. A runtime refusal reached a caller on the other side of
the boundary. Grace mode is the shape that survives it: **production serves and reports; CI
refuses.** Fail **closed** on the default — a policy that defaults to permissive silently loses the
invariant on every host that does not know the property exists, and a green wall cannot show you
which hosts those are.

## Why the staleness bound makes this urgent rather than theoretical

Before an age bound, a stale pin merely drifts and these tests stay dormant. After one, the pin
**will** move within its bound — which converts every latent contract-encoding test from *dormant*
into *scheduled*. They arrive together, on whichever PR moves the pin, looking like its fault.

That is not an argument against the bound. It is the reason to write the tests in the shape above
*before* the bound starts moving the pin unattended — because the alternative is finding them the
way `memex` found the third one.

## Related

- `Doc/Architecture/ReadingCiSignals` — why an absent or skipped required context reads as satisfied
- `Doc/Architecture/ContinuousDeliveryContract` — the promoted set and why its halves must move together
- Plugins#1252, #1260, #1264 (the two pins and their bounds) · Plugins#1281 (the test shape)
