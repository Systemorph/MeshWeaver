---
Name: Permission API
Category: Documentation
Description: hub.CheckPermission / hub.GetEffectivePermissions — the canonical surface for reactive permission checks in MeshWeaver.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="16" r="1"/><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Permission API

Two methods cover the vast majority of permission work in MeshWeaver. Both are defined on `IMessageHub` and live in the `MeshWeaver.Mesh` namespace.

```csharp
using MeshWeaver.Mesh;

// True / false for the current ambient user
IObservable<bool> canEdit = hub.CheckPermission(nodePath, Permission.Update);

// Full effective Permission set for the current user
IObservable<Permission> perms = hub.GetEffectivePermissions(nodePath);

// Explicit user — admin tooling, server-to-server
IObservable<bool> canTheyEdit = hub.CheckPermission(nodePath, "alice", Permission.Update);
IObservable<Permission> theirPerms = hub.GetEffectivePermissions(nodePath, "alice");
```

All overloads return `IObservable<T>`. Compose them with `CombineLatest`, `Select`, and `Where` as you would any other stream. In tests, assert on the stream and let the assertion own the wait — `await hub.GetEffectivePermissions(path).Should().Be(Permission.Read);` — never `.FirstAsync().ToTask()`, which is forbidden in `test/` as well as `src/` (2026-08-30). Never use `await` inside `src/`.
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 280" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="perm-arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".65"/>
    </marker>
  </defs>
  <rect x="20" y="100" width="160" height="52" rx="10" fill="#5c6bc0"/>
  <text x="100" y="121" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">AccessAssignment</text>
  <text x="100" y="140" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">stream</text>
  <rect x="20" y="190" width="160" height="52" rx="10" fill="#26a69a"/>
  <text x="100" y="211" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">MeshNode</text>
  <text x="100" y="230" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">data stream</text>
  <rect x="260" y="80" width="170" height="52" rx="10" fill="#1e88e5"/>
  <text x="345" y="101" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">CheckPermission</text>
  <text x="345" y="120" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">IObservable&lt;bool&gt;</text>
  <rect x="260" y="170" width="170" height="52" rx="10" fill="#1e88e5"/>
  <text x="345" y="191" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">GetEffective</text>
  <text x="345" y="210" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">Permissions</text>
  <rect x="510" y="125" width="160" height="52" rx="10" fill="#f57c00"/>
  <text x="590" y="146" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">CombineLatest</text>
  <text x="590" y="165" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">live, reactive view</text>
  <line x1="180" y1="126" x2="258" y2="106" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="180" y1="216" x2="258" y2="196" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="180" y1="216" x2="508" y2="170" stroke="currentColor" stroke-opacity=".35" stroke-width="1.5" stroke-dasharray="5,4" marker-end="url(#perm-arr)"/>
  <line x1="430" y1="106" x2="508" y2="142" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="430" y1="196" x2="508" y2="158" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <text x="380" y="40" text-anchor="middle" font-family="sans-serif" font-size="12" fill="currentColor" fill-opacity=".5">role revoked → AccessAssignment re-emits → permission stream re-emits → view updates</text>
</svg>

*Permission and data streams both feed `CombineLatest` — a role change anywhere propagates automatically.*

## Enabling access control

```csharp
builder.AddRowLevelSecurity();
```

This single call activates the row-level security pipeline. Without it, `hub.CheckPermission` always emits `true`, so call sites work identically whether the mesh is gated or not — useful for lightweight dev setups where security is not yet configured.

## Composing permission streams with data streams

Permission observables are live. A revoked role propagates through the underlying `AccessAssignment` stream and re-emits automatically — no manual polling, no cache invalidation needed.

Combine a permission check with a data stream using `CombineLatest` to get a single, consistently-updated view:

```csharp
hub.CheckPermission(nodePath, Permission.Read)
    .CombineLatest(workspace.GetMeshNodeStream(nodePath),
        (canRead, node) => canRead ? RenderContent(node) : RenderAccessDenied(node));
```

Whenever the user's permissions change *or* the node content changes, the combined stream re-emits the correct view automatically.

## Quick reference

| Method | Returns | Use when |
|---|---|---|
| `hub.CheckPermission(path, permission)` | `IObservable<bool>` | Guard a single action for the ambient user |
| `hub.CheckPermission(path, userId, permission)` | `IObservable<bool>` | Admin tooling, server-to-server checks |
| `hub.GetEffectivePermissions(path)` | `IObservable<Permission>` | Render a permission summary for the ambient user |
| `hub.GetEffectivePermissions(path, userId)` | `IObservable<Permission>` | Inspect another user's effective rights |
| `hub.CheckPermissionOutcome(path, userId, permission)` | `IObservable<PermissionCheckOutcome>` | You must tell **denied** apart from **could not decide** |
| `AnonymousGate.Evaluate(hub, path)` | `IObservable<PermissionCheckOutcome>` | The visitor is logged OUT and the answer becomes a redirect, a status code or a message |
| `AnonymousGate.AllowAnonymous(hub, path)` | `IObservable<bool>` | The visitor is logged OUT and "unknown" and "not public" lead to the SAME action (sitemap, SEO metadata) |

### "Denied" vs "couldn't decide"

`CheckPermission` collapses to a `bool`, and a fold that *faults* (a storage hiccup, a hub whose DI scope is disposing) surfaces as `OnError` on the stream — not as `false`. If your call site turns any non-`true` into an "Access denied" screen, an entitled user gets told to request permissions they already hold.

The disposing-hub case is a lifecycle event, not a fault, and the fold treats it as one: every service it needs is resolved **once**, at the call, so a long-lived fold keeps answering after its hub's scope is gone (it re-emits on every `AccessAssignment` change — the subscription outlives the render that built it); and if it still faults with an `ObjectDisposedException` while that scope no longer resolves, it terminates with the typed `HubDisposingException` ("the address may reactivate; retry"), which the layout host, `MessageService` and `CheckPermissionOutcome` already classify as benign teardown. An `ObjectDisposedException` from an unrelated disposed dependency on a *live* scope is still a defect and still faults the fold as one — the classification is gated on the scope probe, never on the type alone (issue #2679).

`CheckPermissionOutcome` is the one place that distinction is made: it classifies the verdict as `Granted` / `Denied` / `Undetermined` (carrying the reason). `IsGranted` is `false` on the undetermined leg, so a consumer that ignores the tri-state still fails **closed**. Use it wherever the UI or the caller reports *why* access was refused; never re-derive the difference from a `false` or an exception message upstream.

🚨 **It always produces exactly one outcome, and that is contract (#2742).** A fold has three terminals, not two: it can emit, it can fault, and it can *complete without ever emitting* — which is what one silent leg of the `CombineLatest` does to the whole fold. That third terminal used to pass straight through as an EMPTY stream, and an empty check is not a refusal anywhere downstream: `AccessControlPipeline` ends its decision chain in `.Take(1).Select(…).DefaultIfEmpty()`, whose `null` means "no check refused ⇒ every check was granted", so the message was delivered. `CheckPermissionOutcome` now materialises that terminal as `Undetermined` too. If you write your own consumer of the raw evaluator, apply the same rule: **treat "no outcome" as no verdict, never as consent.** See [AccessControl → The convergence contract](/Doc/Architecture/AccessControl).

`Permission.None` is a special case in both overloads: it short-circuits to `true` without consulting the evaluator.

### The anonymous gate is tri-state too (#2901)

`AnonymousGate` is the one permission decision a **logged-out** visitor triggers — the SEO head, the sitemap, the content route at `/api/content`, and the Blazor navigation gate all ask it the same question: *may an anonymous visitor read this node?* It used to answer it with

```csharp
hub.CheckPermission(path, WellKnownUsers.Anonymous, Permission.Read)
    .Catch<bool, Exception>(_ => Observable.Return(false));   // ❌ the shape this page forbids
```

which is exactly the swallow `CheckPermissionOutcome` exists to replace, sitting on the surface where it is most expensive. A faulted or silent fold became a bare `false`, and the caller — having nothing else to go on — redirected the visitor to `/login` or answered 404. Nothing was logged, nothing was retryable, and monitoring could not tell a degraded permission fold from a page that is simply private.

**The direction of failure is not symmetric here, and that is the whole design.** This gate decides what *the public* may see, so the two wrong answers are wrong in different ways:

| Fold outcome | Wrong answer | Why it is wrong | Correct answer |
|---|---|---|---|
| Undetermined | *granted* | serves private content to the internet | — |
| Undetermined | *denied* | tells an entitled visitor the page is not for them, and hides a degraded dependency behind a routine-looking bounce | **unavailable, retryable** — serve nothing, assert nothing, 503 on an API route |
| Denied | *unavailable* | every gated page starts answering "temporarily unavailable" and retrying forever | *denied* — `/login`, or 404 on a content route |

So `AnonymousGate.Evaluate` returns the `PermissionCheckOutcome` and callers branch on `IsUndetermined` **before** they branch on `IsGranted`. `IsGranted` is `false` on the undetermined leg, so the fail-closed direction holds even for a caller that ignores the tri-state — which is what makes the boolean projection safe to keep:

```csharp
AnonymousGate.Evaluate(hub, path)
    .Take(1)
    .Subscribe(outcome =>
    {
        if (outcome.IsUndetermined) ServeUnavailable();   // 503 / retry — never /login
        else if (outcome.IsGranted) Serve();
        else RedirectToLogin();                            // a verdict was reached
    });
```

`AllowAnonymous(hub, path)` stays as `Evaluate(...).Select(o => o.IsGranted)` — legitimate wherever "unknown" and "not public" lead to the same correct action and nothing is asserted to a human: **omitting** a page from the sitemap, **withholding** SEO metadata. Omission is not a lie; a redirect is.

**No evaluator registered ⇒ `Denied`, definitively — not undetermined.** When no `EffectivePermissionsDelegate` is installed the gate still refuses, and that refusal *is* a verdict: an ungated mesh has no way to express an anonymous grant, so nothing on it is anonymous-readable and no retry changes that. Calling it undetermined would make every unsecured deployment answer a permanent 503 — the same lie pointed the other way. "Deliberately ungated" versus "somebody forgot `AddRowLevelSecurity()`" is a separate statement with its own type, `UnsecuredMeshDeclaration`.

**One violator of the ban is left, and it is not in this repo.** `BlazorHostingExtensions.AllowContentRead` (MeshWeaver.Plugins) still carries the same `.Catch(_ => Observable.Return(false))` on the authenticated leg of `/api/content`, and its sibling defect puts the `.Catch` *inside* `.ToTask(context.RequestAborted)`, so an ingress abort that beats the read budget cancels outside the classifier and logs nothing at all. Until both are ported, a `/api/content` request whose permission fold reaches no verdict still answers 404 instead of a retryable 503. Tracked as MeshWeaver.Plugins#1078; this repo's `PermissionSwallowRatchetGuard` holds `src/`, `memex/` and `samples/` at zero and deliberately carries no allow file, because anything that genuinely cannot reach a verdict already has a name for that.


## See also

- [AccessControl](/Doc/Architecture/AccessControl) — `AccessAssignment` node shape and recursive scope walk.
- [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation) — how identity flows across hub boundaries.
