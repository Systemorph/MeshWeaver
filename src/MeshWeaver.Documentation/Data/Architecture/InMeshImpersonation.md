---
Name: In-Mesh Impersonation
Category: Documentation
Description: Who may act as the platform. User-authored code that the mesh compiles at runtime, and the gates on the trusted gRPC port, can still act as System. This page covers the log-only guards that measure who does, the blast radius measured across the fleet, and the decision options for closing it.
---

The broad-grant guard ([Access Control](/Doc/Architecture/AccessControl) → *Broad grants only through a governed activity*) stops **System** from writing broad access. It does not stop anyone from **becoming** System. Two paths still let user-authored code do that:

1. **In-process code that the mesh compiles at runtime.** This covers NodeType `Source/*.cs`, C# Code nodes and scripts, and NodeType configuration scripts. Such code can call `AccessService.ImpersonateAsSystem()` or `ImpersonateAsHub(...)`, switch to a hand-built System context, or stamp one on a post. After that it can do everything System can, except write a broad grant.
2. **The trusted gRPC port.** A co-deployed gate (the python or node kernel) authenticates by reachability alone. Any delivery it injects without a user runs as **System**. A delivery that carries a context passes through unchanged, so the user code the gate executes can claim System, a hub, or any user.

This page prepares the decision to close these paths. Nothing here enforces anything yet: both guards ship **log-only**, so production data can show who would break before anyone flips them.

## What the log-only guards do

### In-mesh impersonation guard (core)

`InMeshImpersonationGuard` is owned by the mesh-scoped `AccessService`. It checks the **caller** of every impersonation surface:

| Surface | Checked when |
|---|---|
| `AccessService.ImpersonateAsSystem` / `ImpersonateAsSystemFor` / `ImpersonateAsHub` | always |
| `AccessService.SwitchAccessContext` / `SetContext` / `SetCircuitContext` / `SetHostIdentity` | when the new context is a platform principal (System or hub-shaped) |
| `ImpersonationScopeExtensions.RunAsSystem` / `RunAsHub` / `RunAs` | at **composition**, against the code that composed it. `RunAs(resolver)` is checked when the scope opens, against the caller captured at composition |
| `PostOptions.WithAccessContext` / `ImpersonateAsHub` | at `hub.Post`, against the code that set the option |
| `AccessContextScope.AsSystem` / `FromNode` (Mesh.Contract) | always for `AsSystem`. For `FromNode`, when the node's author resolves to a platform principal or the method falls back to System |
| `ContentImportBuilder` / `SyncContentFilesBuilder` `.ImpersonateAsSystem()` / `.WithAccessContext(...)` | when the declared identity is a platform principal |

**How a caller is classified.** The guard looks at the `AssemblyLoadContext` of the calling method's assembly:

- **Platform:** the default context, or any context marked `IPlatformLoadContext` (for example, the Orleans modules context).
- **In-mesh:** every other context. This includes `DynamicNode_{node path}` for a NodeType, `kernel-script-session` for scripts, `node-config-script:{path}` for configuration scripts, and any context the code built for itself.

The fast path reads only the immediate caller (`Assembly.GetCallingAssembly()`, one frame). That keeps the cost on the hot platform paths at one frame. The guard walks the stack only when that frame is the BCL (reflection, or an Rx operator invoking a method group) or is in-mesh. The walk steps past the surface and the BCL to the first frame that decides.

**Modes:** `Access:InMeshImpersonation:Mode`.

| Mode | Effect |
|---|---|
| `LogOnly` (default) | Every in-mesh impersonation is allowed and logged. The log line is `[InMeshImpersonation] WOULD REFUSE {surface} as {principal} by {DynamicNode_…} ({Type.Method}, assembly …); occurrence N, mode LogOnly`. |
| `Enforce` | An untrusted in-mesh caller gets `InMeshImpersonationRefusedException`, an `UnauthorizedAccessException` that names the caller. The catalog key is `access.impersonation.inMeshRefused` (en + de). |
| `Off` | No check. |

An unknown mode value is logged and runs as `LogOnly`.

**Trusted code:** `Access:InMeshImpersonation:TrustedCode:0..n` lists node-path prefixes, such as `Governance` or `Store`. A prefix matches a whole path segment (`Governance` does not match `GovernanceX`), and it matches only NodeType contexts: a script session is never trusted. A trusted caller is logged as `TRUSTED` at Information. The trust is only as strong as the write protection on that namespace.

**Log volume.** The first occurrence of each (verdict, surface, principal, caller) is logged, then every 1000th, with the running count. A handler that impersonates on every message shows up with its rate and cannot flood Loki.

The inventory is one Loki query: `|= "[InMeshImpersonation]"`.

### Gate principal (MeshWeaver.Plugins, `MeshWeaver.Hosting.Grpc`)

| Setting | Values |
|---|---|
| `Grpc:GateIdentityMode` | `LogOnly` (default) or `Enforce` |
| `Grpc:GatePrincipal` | default `gate-service` |

A trusted-port delivery is affected when it carries **no user**, or carries a **platform principal** (System, or a hub address the gate's code can type as easily as anything else):

- **`LogOnly`:** the delivery keeps System and is logged as `[GatePrincipal] WOULD STAMP gate-service instead of system-security ({reason}) for {MessageType} from {Sender} to {Target}`.
- **`Enforce`:** the delivery runs as the gate principal. That is an ordinary identity with only the grants it is given (AccessAssignments to `gate-service`), never System.

A carried ordinary user still passes through in both modes, because that is the gate executing that user's request.

A gate principal that names a platform identity is refused when the registry is constructed.

## Measured blast radius

This was measured on 2026-10-01. Each repo was read with `git grep` on its remote default branch, comments excluded. An "escalation call" is any call that installs System or a hub.

| Repo, in-mesh trees only | Escalation calls | Files |
|---|---|---|
| **Core** (`samples/`, `src/*/Data/`) | **0** | 0 |
| **MeshWeaver.Plugins** (every node folder; `src/` excluded) | **270**: 149 local `AsSystem(...)` wrappers, 69 `SystemIdentity.Run`, 26 `ImpersonateAsSystem`, 14 `MeshQueryRequest.AsSystem()`, 8 `AccessContextScope.AsSystem`, 2 `ImpersonateAsSystemFor` | 104 |
| **MeshWeaver.Reinsurance** | 5 `ImpersonateAsSystem` | 4 |
| **MeshWeaver.SocialMedia** (`src/` excluded) | 1 `ImpersonateAsSystem` + 3 wrapper calls | 2 |
| **MeshWeaver.Crm** | 1 `ImpersonateAsSystem` | 1 |
| **Education, Manufacturing, Memex** (`mesh/`, `samples/`, `deployments/`) | 0 | 0 |

The in-mesh tests in Plugins add 53 `ImpersonateAsSystem`, 29 `RunAsSystem`, 29 `SystemIdentity.Run` and 114 wrapper calls. They compile in the mesh too, under the same package roots.

None of the 16 in-mesh `SwitchAccessContext` sites escalates: each switches back to a captured viewer. The guard only looks at a switch whose target is a platform principal, so these sites never reach it.

**The live mesh was NOT swept, and that is a failed sweep, not a clean one.** On both portals, `search_chunks` answered `"searched": false` to all eight unscoped queries. A scoped probe answered `searched: true`, but its index covers content collections, not Code nodes, so it is the wrong instrument. A semantic `search … partitions:all` over `nodeType:Code` found 93 nodes over 140 readable partitions on the public portal. Spot checks on that list found three user-authored Code scripts that open `Observable.Using(() => access.ImpersonateAsSystem(), …)`:

| Script | What it does as System |
|---|---|
| `rbuergi/Scripts/FixGmailPartition` | creates a User root plus an **Admin** AccessAssignment |
| `rbuergi/Scripts/OffboardGmail2` | deletes a partition root |
| `rbuergi/Script/InviteYouTubeTeam` | creates GrantSpaceAccess subscriptions |

Each of these is exactly the escalation this page is about. **The live total is unknown.** The `LogOnly` run of the guard is the instrument that measures it, because it sees every caller that actually executes on a replica, whoever owns the partition.

### What legitimately needs System, and the sanctioned alternative each would need

Every in-mesh Plugins NodeType group that impersonates, from the sweep:

| NodeType group | Why it writes as System | Sanctioned alternative if denied |
|---|---|---|
| **Store/** (Core, Catalog, Licensing, Installer, Publishing, InstallRequest, Order, Subscription, Review, Maintenance, BillingProfile, Plugin) | Entitlements, ledger, coupons and grants into package and Admin partitions; installs into users' partitions; the payment-webhook inbox; writes for anonymous visitors | Trusted-code entry (`Store`), or a platform enrollment service. The broad-grant half already goes through `store.enroll` |
| **Hosting/** (Deployment's TriageIntake, BugFixPool, PullRequestIntake, SelfUpdateRouting, PlatformBuildInboxWatcher, …; InstanceAction, InstanceRequest, Issue, Build, PrWatch, TriageItem, ModuleInventory) | Inbox, webhook and timer watchers with no user behind them; writes into `Ops`, `Deployments` and the Admin bell; recycles | Trusted-code entry (`Hosting`). These are control-plane infrastructure, authorized upstream (`SystemInstall.Authorize`) |
| **Governance/** (Activity, NameCheck) | The governance executor (`ImpersonateAsSystemFor(governedBy)`), name-check reviews | Trusted-code entry (`Governance`). It is the one way through for broad grants |
| **Essentials/OperationRequest** | Runs an **approved arbitrary script as System** | Trusted, but this is the highest-power site: approval is the only gate. It deserves a governed standard of its own |
| **Feedback/** (Feedback, EvalCase, Digest) | Writes into the shared Feedback space the viewer cannot write | Trusted-code entry (`Feedback`), or a submission grant for every signed-in user |
| **Edu/** (AnswerSheet, CourseInvite, LearningJourney, CourseCatalog); **Chess/Game**; **RemoteControl/Screen** | Provision the viewer's **own** partition, then write the viewer's own node | A **self-provisioning API**: `EnsurePartitionProvisioned(viewer)` as the platform, with the write as the user. With that API, no trust entry is needed |
| **Publish/Slide** | A live sibling-slides query opened as System, which **bypasses RLS on a read** | None: this looks like an escalation and should run as the viewer |
| Reinsurance `ReinsuranceDemo/Installer`, `Underwriting/Submission/ClaimsReviewArea` | Demo seeding; an enquiry node created from a layout area as System | ClaimsReviewArea looks like an escalation (a user action written as System). The demo installer is a trusted-code entry |
| SocialMedia `LinkedIn/TileMigration` | Converges other users' profile tiles | Trusted-code entry, or a governed migration |
| Crm `CrmSystem` | Migration control plane re-typing client roots the admin does not own | A governed migration |

Separately, four satellite sites still use raw `Observable.Using(() => access.ImpersonateAsSystem(), …)`. That is the #1790 latch, which leaves System on the subscribing thread. The four sites are Reinsurance ×2, SocialMedia and Crm. Plugins' `check-impersonation.py` runs only in Plugins CI, which is how these four got in.

## What the runtime guard cannot see

These limits were measured, not reasoned. They are why `Enforce` on the runtime guard alone is **defence in depth, not a boundary**:

1. **A tail call removes the in-mesh frame.** A method whose body *ends* in the surface call, such as `IDisposable Go(AccessService a) => a.ImpersonateAsSystem();`, may be compiled by the release JIT as a tail call. When that happens, the in-mesh frame is gone from the stack. Measured in `InMeshImpersonationGuardTest`: the first draft's expression-bodied `SetSystemContext` and `RunAsSystem` passed the guard under `Enforce`. The guard then judges the frame below. That frame is still in-mesh when user code called the method, but it is platform code when the platform invoked a user delegate. The commonest in-mesh idiom, `Observable.Using(() => access.ImpersonateAsSystem(), …)` returned to the platform, is exactly that shape.
2. **A delegate handed to a platform subscriber.** Once no in-mesh frame is left on the stack, the subscribe-time call looks like platform code.
3. **A hand-built `AccessContext` passed to an API that is not a surface.** Three examples:
   - `IMessageDelivery.SetAccessContext(...)` followed by `hub.DeliverMessage(...)`.
   - `MeshQueryRequest.AsSystem()`, or any `UserId` value. This is an RLS bypass on reads, and the request carries the user id as plain data.
   - Any future API that accepts a context.
4. **A gate's passthrough of a carried ordinary user.** The gate principal closes System and hub identities. A gate can still claim to be **any user**, a global admin included, because the trusted port passes a carried non-platform identity through.

## Decision options

| | Option | Closes | Cost / blast radius |
|---|---|---|---|
| **A** | Status quo: keep the broad-grant guard only | Broad grants only | Nothing breaks. In-mesh code keeps every other power System has |
| **B** | **Runtime guard + gate principal (these drafts) → `Enforce`**, with `TrustedCode` = the package roots that need it (`Store`, `Hosting`, `Governance`, `Feedback`, `Essentials`, …) | Direct impersonation by user-authored NodeTypes and scripts in user partitions and Spaces; gate deliveries without a user | Low once the trust list matches the `LogOnly` inventory. Leaves limits 1–4 open, so it is a measurement and a speed bump, not a boundary |
| **C** | **B + a compile-time check**: when the portal compiles in-mesh code, refuse (under Enforce) any **symbol reference** to an impersonation surface, `WellKnownUsers.System`/`SystemContext`, `IMessageDelivery.SetAccessContext` or `MeshQueryRequest.AsSystem`, unless the NodeType is trusted | Limits 1–3 for compiled code: the compiler sees the reference however the JIT emits the call. Reflection by name stays open unless `System.Reflection` is also banned there | Medium. It must report through `compilationStatus` and the warning ratchet, **never** through a `GeneralDiagnosticOption` in `EmitPipeline` (that would park types and stall a roll; see [In-Mesh Warning Standard](/Doc/Architecture/InMeshWarningStandard)). It needs the same trust list |
| **D** | **Sealed platform principal**: System and hub contexts carry an unforgeable seal (an object only platform assemblies can mint); `SecurityService` treats an unsealed `system-security` as an ordinary, grant-less id; transports re-seal on ingress | Every forgery path, by construction, including hand-built contexts and reflection | High. Every serialization hop (the Orleans silo boundary, packaging) must re-seal; it touches the identity model fleet-wide |
| **E** | Bind the gate's passthrough to its in-flight requests: a carried user is honored only when it matches the context of a request the gate received and has not yet answered | Limit 4 | Medium, and confined to Plugins' gRPC registry |

**Recommendation:** run **B** in `LogOnly` for one week and size `TrustedCode` from the `[InMeshImpersonation]` and `[GatePrincipal]` lines. Before flipping, fix the escalation-shaped sites: Publish/Slide, ClaimsReviewArea, the self-provisioning group, and the user scripts above. Then flip **B** to `Enforce` and schedule **C** as the boundary, with **E** for the gate. **D** is the end state if forged contexts are ever measured in practice.

## Verifying the guards

- `test/MeshWeaver.Messaging.Hub.Test/InMeshImpersonationGuardTest.cs` loads a copy of the test assembly into a collectible context named like a NodeType's. Under `Enforce`:
  - Calls from that copy are refused: direct calls, `RunAsSystem` composition, a switch or `SetContext` to System, and calls through reflection.
  - A switch to an ordinary user passes.
  - A trusted prefix passes, and a near-miss prefix does not.
  - A script session is never trusted.

  Under `LogOnly`, the in-mesh caller is allowed and named in the log, and platform callers are not logged.

  The **negative control** runs the identical methods from the default context under `Enforce`, and they pass. A mutation that turns off the load-context classification turned 7 of the 10 cases red.
- `MeshWeaver.Hosting.Grpc.Test` / `MeshGrpcTransportTest` (Plugins) covers the gate:
  - Under `Enforce`, a delivery with no user, a carried System and a carried hub address all run as `gate-service`, while a carried user still passes through (the control).
  - The default keeps System, which is the negative control.
  - A platform gate principal is refused.

  A mutation that disabled the stamp turned the `Enforce` case red.
