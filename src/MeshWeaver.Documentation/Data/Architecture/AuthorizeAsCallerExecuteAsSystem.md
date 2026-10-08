---
nodeType: Markdown
name: Authorize as Caller, Execute as System
category: Architecture
description: >-
  A control-plane operation (an instance action and its approval re-plan, a governance pass, a sync,
  an operator step, a watcher acting on a request node) checks the CALLER's access explicitly — when
  the request is written and again right before it executes — and then executes as System. Why the
  two are separated, the two checkpoints, the code shape, what is not allowed, and the incident that
  produced the rule.
icon: /static/NodeTypeIcons/box.svg
---

# Authorize as Caller, Execute as System

> **A control-plane operation separates AUTHORIZATION from EXECUTION. Check the caller's access
> EXPLICITLY — when the request is written, and again immediately before it executes — and refuse
> fail-closed, naming the missing permission. Then execute under System. Never let the caller's
> row-level-security reach decide, mid-execution, whether an operation that was already authorized
> can finish.** Policy `authorize-as-caller-execute-as-system` ([register](../PolicyNotProse)).

## What it covers

A **control-plane / system operation** is work the platform performs *on behalf of* a request that
a person (or a service identity) filed, where the work itself touches records that belong to the
platform rather than to that person:

- a `Hosting/InstanceAction` — Provision, Roll, Reconcile, Restart, Suspend, … — **and its approval
  re-plan**;
- a governance pass (a policy evaluation, a release-readiness gate, a broad-grant activity);
- a source sync or import;
- an operator step executed against a deployment record;
- any watcher that reacts to a `RequestedX` field on a request node and acts on it.

The records these operations read and write — a GitSynced `Deployments/<name>`, a registry entry,
an operator status node — are system-owned. The person who filed or approved the request is
**authorized to ask for the operation**; that does not mean they can read every record the
operation must read to carry it out, and it should not have to.

## The two checkpoints

### 1. When the request is WRITTEN

The create/update of the request node is validated, and an unauthorized request is **refused by
name** at that write — it never gets parked as a pending request that nobody can execute.

- The check is an `INodeValidator` (or an `INodeTypeAccessRule` /
  `INodeTypePermissionRule` the request's NodeType declares) on `Create` and `Update`, evaluated as
  the CALLER.
- The refusal names the permission and the target: *"User 'alice' lacks Update permission on
  'Deployments/control' — cannot request Roll"*, not a generic "Access denied".
- An approval is a write too: an approver who lacks the right to approve is refused at the approval
  write, not at execution.

### 2. Immediately BEFORE executing

The caller's access is checked **again**, explicitly, right before the operation runs. Between the
request and its execution the requester or the approver may have lost the right (a revoked grant,
a removed membership, a role change). The check uses the Permission API against the identity
recorded on the request, not the ambient context of whatever thread picked it up:

- `hub.CheckPermissionOutcome(path, userId, permission)` — the tri-state form, so *could not
  decide* (`Undetermined`) is reported as such and still fails closed. See
  [Permission API](../PermissionApi).
- On `Denied` or `Undetermined` the operation does not start; its status records the refusal with
  the permission, the path and the identity checked.

## Then: execute as System

Once both checkpoints pass, everything the operation does — reading the records it acts on,
rendering plans, writing results and status — runs as **System**. The operation's success must not
depend on what the caller can read.

```csharp
// 1) explicit authorization, as the recorded caller — fail closed, name the permission
hub.CheckPermissionOutcome(request.DeploymentPath, request.RequestedBy, Permission.Update)
    .SelectMany(outcome => outcome.IsGranted
        // 2) execution as System — reads, plan rendering and result writes
        ? access.RunAsSystem(() => ExecuteAction(hub, request))
        : RecordRefusal(hub, request, outcome,
            $"User '{request.RequestedBy}' lacks Update permission on '{request.DeploymentPath}'"))
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Action {Path} failed", request.Path));
```

- `AccessService.ImpersonateAsSystem()` is the primitive; in a reactive pipeline compose it through
  `access.RunAsSystem(() => work)` (`ImpersonationScopeExtensions`), which opens and closes the
  scope inside one synchronous `Subscribe`. Never `Observable.Using(() => access.ImpersonateAsSystem(), …)`
  — its store and restore land on different threads and latch System onto the subscriber
  ([Access Context Propagation](../AccessContextPropagation)).
- Where the operation is the executor of a governed activity, say so:
  `access.ImpersonateAsSystemFor(governedBy: <activity path>, onBehalfOf: <user>)`, so the
  broad-grant guard and the audit trail see who the System write serves.
- A single infrastructure post can carry System as a value instead:
  `o.WithAccessContext(WellKnownUsers.SystemContext)`.
- Results that record WHO asked (`requestedBy`, `approvedBy`) carry the caller's id as data on the
  result — the write itself is System's.

## What is NOT allowed

- **Impersonation as a substitute for the check.** Running as System without the explicit check is
  privilege escalation: every caller who can write a request node gets System's reach. The check is
  the authority; System is only the executor.
- **Checking only at execution.** An unauthorized request must be refused at its write. Parking it
  and refusing later leaves a request that looks pending and can never run.
- **Checking only at the write.** Rights change between request and execution; the pre-execution
  check is not optional.
- **Executing as the caller.** A plan render, a re-plan after approval, or a status write that runs
  under the requester's or approver's identity makes the operation depend on that person's RLS
  reach over system-owned records. That is the incident below.
- **Inferring permission from a failed read.** A `Not found` or `lacks Read permission` from a read
  inside the operation is not an authorization verdict — the verdict is the explicit check.
- **Application writes on a user's behalf.** This rule does not widen
  [Access Context Propagation](../AccessContextPropagation): a user editing their own data, a view
  writing back a field, a thread posting a message — those still carry the user's identity end to
  end. This page covers only control-plane operations whose authority was checked explicitly.

## Evidence

On the control instance (`control.systemorph.com`), the action
`Ops/Actions/provision-control-registry-20261008` was **approved and then refused** at
2026-10-08 07:39:40Z:

> the Hosting/Deployment record 'Deployments/control' is listed by the index but could not be read —
> User 'rbuergi' lacks Read permission on 'Deployments/control'

The approval re-plan ran **as the approver**. The approver was authorized to approve the action; the
deployment record is GitSynced and system-owned, and the approver held no Read grant on it. The
operation failed after authorization, on a read its executor should have performed as System. Under
this rule the re-plan checks the approver's right to approve explicitly, then renders the plan as
System.

## Related

- [Access Context Propagation](../AccessContextPropagation) — application writes carry the user's
  identity; the sanctioned System exceptions.
- [Owner Injection](../OwnerInjection) — the standing identity when a node's own hub acts with no live
  caller.
- [Permission API](../PermissionApi) — `CheckPermission` / `CheckPermissionOutcome`.
- [In-Mesh Impersonation](../InMeshImpersonation) — who may act as the platform at all.
- [Policy Not Prose](../PolicyNotProse) — the register entry for this rule.
