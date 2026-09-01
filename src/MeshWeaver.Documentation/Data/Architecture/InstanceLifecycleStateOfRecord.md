---
Name: Instance Lifecycle — State of Record
Category: Architecture
Description: What the new-instance setup and registry-key-rotation programs actually built, measured against the code, which repo owns each half, and exactly what is left. Written so the remaining work is pickable-up cold.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M3.5 9h4"/></svg>
---

Two programs govern how an instance comes into existence and how its credential is replaced: the
**new-instance setup** flow, and **registry-key rotation through the hosting operator**. Both were
tracked as issue threads; this page replaces them, because the interesting content is *which half
lives where*, and that answer is unobtainable from either issue.

**Everything below was measured against the code on 2026-09-01.** Where a symbol has left this
repository that is stated explicitly, because "not implemented" and "implemented elsewhere" take
very different next steps and the two are indistinguishable from a failed grep.

> 🚨 **The single most useful fact on this page:** most of both programs is **not in core**. The
> instance-action control plane, the rotation verb, the deployment-record model and the setup GUI
> all live in the plugins repository or the operator scripts. A core session that greps for them and
> finds nothing will conclude "not started" and be wrong.

## New-instance setup — phase 1 shipped, and stops one step short of being reachable

| Element | State | Where |
|---|---|---|
| The durable instance manifest | **Implemented** | core — carries the storage selection, boot module set, provision set and per-user pre-install set |
| Manifest is written atomically | **Implemented** | core — a crash mid-write cannot strand an instance in setup |
| "No storage configured" is a legitimate boot state | **Implemented** | core — booting an empty image no longer throws; it marks the mesh as awaiting setup and returns early |
| Boot actually consumes the manifest's storage answer | **Implemented** | core |
| Boot consumes the manifest's **module / package / pre-install** answers | **Not started** | those three fields have **no readers anywhere** |
| Anything reads the awaiting-setup flag | **🚨 Not started** | core — the flag is set and **nothing consults it** |
| Storage-backend discovery for the wizard to offer | **Not started** | factories are registered keyed by type name; keyed DI has no "list all keys" surface and nothing supplies one |
| The wizard GUI | **Not started here** | there is no Blazor project in this repository at all |

**The gap that matters is the flag nobody reads.** An empty image now reaches the awaiting-setup
state instead of throwing — which was the stated step one — but a host that reaches it returns from
configuration with none of persistence, graph or the plugin catalog wired, and **nothing serves
anything in its place**. The boot no longer fails; it succeeds into a portal that can do nothing and
says nothing about why. That is a strictly worse failure mode than the exception it replaced, and it
is the first thing to fix if this program resumes.

The three composition mechanisms the wizard is meant to *compose rather than reinvent* all exist in
core and all work — configured module installation, package install, and per-user pre-install. None
of them is connected to the manifest. Wiring those three fields is therefore mechanical, and doing
it is what makes the manifest more than an artifact.

## Registry-key rotation — the dangerous half is done and correct

The operator script that mints and stores a new key exists and does the delicate part right:

1. mints the key into a shell variable,
2. writes it to the vault with output suppressed and immediately unsets the variable,
3. reports **only its SHA-256** back over the marker-line channel,
4. waits for the synced secret by comparing **hashes, not values**.

It never prints the key — not on success, not on failure. Its own banner says so, and reading the
script confirms it.

**What it deliberately does not do** is restart the portal, wait for the rollout, or verify. Those
are separate steps meant to be sequenced by the action plan — and that plan is **not in this
repository**, so *this repo cannot prove the ordering is correct*. That is a real limit on what a
core review can assert, and it should not be papered over.

| Element | State | Where |
|---|---|---|
| The mint-and-store script | **Implemented** | core, under the operator scripts |
| The rotation **verb** and the action plan that sequences it | **Elsewhere** | plugins repository |
| The marker-line parser that reads the job log back into the mesh | **Elsewhere** | named only in a shell comment here; no implementation in core |
| The deployment-record model and its values rendering | **Elsewhere** | no such type exists in core |
| Adopting a rotated key's hash | **Implemented, no production caller** | core — exercised only by tests |
| Re-issuing a key | **🚨 Dead code** | core — zero callers of any kind |
| Behaviour tests for the rotation script | **Not started** | every *other* vault command has argument, refusal and injection tests; this one has none |

Two consequences worth acting on. First, **the operator's own gate does not know the rotation script
exists** — it checks that every command the plan can emit is present, from a hand-maintained list,
and the list has not learned about this one. A gate whose coverage is narrower than its subject is
the failure mode this platform keeps re-learning, so the honest fix is to assert in the *other*
direction as well: every script that ships must be either claimed by the plan or explicitly declared
as not yet wired. Second, the re-issue path is dead code that reads like a supported mechanism;
leaving it there invites someone to call it and discover it was never finished.

## What a core session can and cannot settle

A recurring cost in both programs is reaching a confident wrong answer from an absent grep. The
split, stated once:

- **Core owns:** the manifest and its boot seam, the key-adoption path, the operator scripts and
  their gate, the composition mechanisms the wizard would drive.
- **The plugins repository owns:** the instance-action control plane and every lifecycle verb, the
  deployment records, the portal hosts, and all UI.
- **The private deployments repository owns:** the actual values for the running portals — so
  whether a given key is set in production **cannot be answered from here at all**, and any claim
  that it is or is not must name where it was measured.

## See also

- [Instance Identity and Setup](../InstanceIdentityAndSetup) — the design this program is delivering
- [Modules](../Modules) — the activation list and why it is read before DI exists
- [Deployment](../Deployment) — the deployment routes index
- [The Compile Program — State of Record](../CompileProgramStateOfRecord) — the sibling audit
