---
nodeType: Markdown
name: Sole-Maintainer Approval
category: Architecture
description: >-
  Every approval in the platform is four-eyes — the approver is not the requester — with ONE
  declared, audited exception: the installation's maintainer may approve a request they filed
  themselves. One rule shared by the instance-action, operation-request and governed-activity gates,
  where the maintainer is declared, how a self-approval is stamped and shown, and the approvals inbox
  that approves in bulk through each item's own path.
icon: /static/NodeTypeIcons/box.svg
---

# Sole-Maintainer Approval

> **An approval is four-eyes: the approver is never the requester. The ONE exception is the
> installation's DECLARED maintainer, who may approve a request they filed themselves — and every
> such approval is stamped on the node, written to its log and shown on its page as "self-approved
> by maintainer", who and when. Everyone else still needs a different approver.** Policy
> `sole-maintainer-approval` ([register](../PolicyNotProse)).

## Why

A single-maintainer estate runs its agents through the maintainer's own credential, so nearly every
pending request — a Reconcile, a Recycle, an operation request — is filed under the maintainer's
identity. Four-eyes then leaves the estate with nobody who may approve anything at all. The
alternatives are worse: an agent identity that approves on the maintainer's behalf moves the decision
to a machine, and a standing admin bypass makes it silent. The exception is therefore NAMED (one
person), DECLARED (configuration a governed change controls, never a node any administrator can
edit), and AUDITED (it can never look like a second person's approval).

## The rule — one function, three gates

The decision lives in ONE place, `SoleMaintainerApproval.Decide(requester, approver, maintainer)`
(MeshWeaver.Plugins `Store/Core/Source/SoleMaintainerApproval.cs`), shared by source into every gate:

| answer | when | what the gate does |
|---|---|---|
| `Other` | the approver is not the requester, or either is unknown | the ordinary case — every other check of the gate applies |
| `SelfApprovedByMaintainer` | approver = requester = the declared maintainer | admitted, and STAMPED (below) |
| `SelfApprovalForbidden` | approver = requester, not the declared maintainer | refused: a second person must approve |

Identities are compared trimmed and case-insensitively. The rule decides ONLY the self-approval
question: every gate's global-administrator check, binding, freshness and single-use checks are
unchanged.

| gate | where it calls the rule | who counts as the requester |
|---|---|---|
| `Hosting/InstanceAction` | `ActionsExecutor.ApprovalGate`, `ApprovalNotice.EligibleApprovers` | the recorded requester (`RequesterOf`) |
| `Essentials/OperationRequest` | `OperationRequestContent.ApprovalRefusal` / `RelationOf` | BOTH the typed `requester` and the framework-stamped creator — an agent filing under a person's credential types itself as requester, and the credential's owner is still its author |
| `Governance/Activity` | `ActivityGates.SignatureRefusal`, `FromSignatureRequest` | the proposer; the maintainer is the STANDARD's own `authority.maintainer` |

## Where the maintainer is declared

The installation's maintainer is the configuration key `Hosting:Operator:Maintainer`, which the
deployment record's `operator.maintainer` renders (`Hosting__Operator__Maintainer`;
`HostingOperatorSpec.Maintainer`). The key kept its historical name, from when it covered
only the instance-action Actions path. It now holds for every approval gate of the installation:
instance actions on any executor, and operation requests. Absent, nobody may approve their own
request. It is deliberately NOT a node: a node in the `Admin` partition would let
any global administrator name themselves and approve their own requests — the very bypass the rule
exists to prevent. Changing the record is itself a governed Reconcile that someone approves. A
Governance standard may name a narrower maintainer for that standard alone (`authority.maintainer`,
committed content); the decision is the same function either way.

## Audited, never silent

| gate | on the node | in the log | on the page |
|---|---|---|---|
| instance action | `selfApprovedBy` / `selfApprovedAt`, stamped by the watcher in the write that starts the approved run; cleared by the next park | `SELF-APPROVED BY MAINTAINER: '<id>' requested … and approved it themselves … (policy sole-maintainer-approval …)` | a warning above the plan and a row in the Summary |
| operation request | `selfApprovedBy` / `selfApprovedAt`, stamped by the control plane when the run starts | the same line, kept at the HEAD of the run log by every later frame | a fact row "Self-approved by maintainer" |
| governed activity | the maintainer's own `signatures` entry (signer, `signedAt`) and the Signatures gate's `detail`, which names `<id> (self-approved by maintainer '<id>' (policy sole-maintainer-approval))` | the gate transition line the activity logs with its timestamp (`gates: …: Green (1/1 — <id> (self-approved by maintainer …))`) | the gate's evidence in the Gates grid, and the signature row |

The page label and explanation are translated (English, German) through
`SoleMaintainerApproval.Label` / `Explanation`; the log line is machine-facing English and UTC.

## The approvals inbox

`Hosting/Approvals` (NodeType `Hosting/ApprovalInbox`) lists every item on THIS instance that waits for
an approval and that the viewer can read: parked instance actions, approvable operation requests, and
open governed activities. One fed grid (`BindGrid` + `PropertyColumnControl`): type, what it does,
plan, filed by, filed, expires, "can you approve it?" and the live result; a row-scoped ☐/☑ selection;
"Approve selected", "Reject selected", "Select every approvable row".

- **No approval path of its own.** An instance action is approved through its page's own Approve body
  (`InstanceActionLayoutAreas.ApproveClick` — bound to the plan the row showed, attested for the
  clicker); an operation request through its `RequestedAction` field
  (`OperationRequestLayoutAreas.RequestActionAs`). The item's own gate re-checks everything.
- **Never offered:** a superseded action (its `supersededBy` field, or a name marked `SUPERSEDED`), an
  expired park, an item the viewer filed and may not approve, and an action that cannot run where it
  lives (a Job-executed kind on an installation whose operator is off, a dispatched kind with no
  signing key). A bulk Approve SKIPS such a row by name even when it is selected.
- **Reject** retires an instance action through its own cancel field (`supersededBy`) and rejects an
  operation request through `RequestedAction = Reject`.
- **Activities are listed, never selected:** a signature is the signer's own act on the activity's
  page.
- **Results are live, per row**, read off each item's own node stream, and stay after the item leaves
  the pending set.
- **One instance.** An instance never reads another instance's requests; each has its own inbox at
  the same address, and the page says so.

Manual for operators: MeshWeaver.Plugins `Hosting/ApprovalsInbox` (`get Hosting/ApprovalsInbox`).
