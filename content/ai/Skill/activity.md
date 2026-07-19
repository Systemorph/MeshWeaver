---
nodeType: Skill
name: /activity
description: Run any external, side-effecting operation — send an email, call Stripe, patch a deployment, fulfill a webhook — as a MeshWeaver Activity with a durable log. The uniform contract for side-effects — reactive (no async on hub threads), I/O through IIoPool, progress and outcome persisted under the partition's _Activity satellite, cancellable. Covers when to use it, the RunActivity recipe, the node-native Code-node form, and the anti-patterns (bare HttpClient on a hub thread, a bespoke control-plane node reinventing the log).
icon: History
category: Skills
order: 12
---

You are wiring up an **external, side-effecting operation** — one that reaches outside the mesh and
*changes something*: send an email, create a Stripe Checkout session, patch a Kubernetes deployment,
fulfill a payment webhook, push to GitHub. In MeshWeaver these do **not** get a bare `async` method or a
one-off `HttpClient` on a hub thread. They run as an **Activity with a log**.

# Why — one contract for every side-effect

An Activity gives you four things a bare async call never will, for free and uniformly:

1. **A durable, queryable log.** The activity is a real node at `{partition}/_Activity/{id}` whose
   `ActivityLog` keeps every `ctx.Log(...)` line and a terminal `Status` (Succeeded / Failed / Cancelled).
   *The log is always kept* — after the fact you can see exactly what happened, who ran it, and why it
   failed. (The memex self-update that patches a live deployment is the poster child: when it silently
   half-ran, there was no log; as an Activity it would have been self-documenting.)
2. **Reactive, off the hub scheduler.** The command is `Func<ActivityContext, IObservable<Unit>>` — no
   `async`/`await` on hub- or view-reachable threads. The one sanctioned async boundary is **`IIoPool`**
   (`Doc/Architecture/ControlledIoPooling.md`); the HTTP/file round-trip runs there, bounded, never on
   the subscribing thread.
3. **Cancellation** via the Activity Control Plane — `RequestedStatus = Cancelled` trips the command's
   `CancellationToken`. (`Doc/Architecture/ActivityControlPlane.md`.)
4. **Correct ownership/routing.** The activity node is created under a real, routable `partitionPath`
   and the command runs as its owner. An **empty/whitespace partition NotFound-storms the router** — the
   same failure mode that makes ad-hoc hub reads from an ephemeral `cache/…` hub time out.

# The recipe — `hub.RunActivity`

```csharp
using System.Reactive;              // Unit
using System.Reactive.Linq;
using MeshWeaver.GitSync;           // RunActivity, ActivityContext
using MeshWeaver.Mesh.Activity;     // ActivityCategory

// partitionPath MUST be a real, routable owning node (a space, a user root, …) — never "".
IObservable<string> activityPath = hub.RunActivity(
    partitionPath: userPath,                     // owner + where the _Activity node lives
    category:      ActivityCategory.DataUpdate,   // or a fitting category constant
    title:         "Send go-to-market email",
    command: ctx =>
    {
        ctx.Log($"Sending to {recipients.Count} recipients…");
        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                   ?? IoPool.Unbounded;
        return pool.Invoke(async ct =>                 // ← the ONLY async, on the IIoPool
                {
                    using var http = /* build client with the caller's delegated token */;
                    using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Graph refused ({(int)resp.StatusCode}).");
                    return Unit.Default;
                })
                .Do(_ => ctx.Log("Sent."));
    });
```

- Subscribe to `GetMeshNodeStream(activityPath)` to watch `ActivityLog.Messages` / `.Status`; cancel with
  `hub.CancelActivity(activityPath)`. A thrown exception in the command is caught by the runner and written
  as the terminal `Failed` status + message — you never lose the reason.
- **Producing a value** (a checkout URL, a message id)? An activity is *fire-and-log*; it does not return a
  value through `RunActivity`. Have the command **write the result onto a node** and let callers read that
  node from the workspace — exactly how `OrderControlPlane` stamps `CheckoutUrl`/`CheckoutSessionId` onto
  the Order after `StripeGateway.CreateCheckoutSession`.

# The node-native form — a Code node run as an activity

The command is ordinary reactive C#, so it can live in a **plugin's `Source/`** (compiled live on the mesh)
or an **executable Code node** rather than in the core image. That is the point: a side-effect authored as a
node-native activity is **iterated on the mesh in seconds — no core redeploy**. Keep only the things that
*cannot* be node-native in core: secrets (OAuth client secrets, API keys read from server config) and
ASP.NET controllers. Everything downstream of "give me a token / a key" is a Code node in an activity.

# When NOT to use it

Not every `IIoPool` call is an activity. **Reads and client-side UX are not activities:**

- Read-only probes — `ProviderModelLister`, `AcrTagLister`, a status fetch. No state changes, no audit value.
- Client-side inference — Whisper/Voice/on-device chat. UI-thread offload, not a server operation.
- Read-only data loaders — CSV/HTTP pulls into a data source. (Log them only if provenance matters.)

Rule of thumb: **if it changes state outside the mesh, or someone will later ask "did it run and what
happened," it is an Activity.** If it only reads, `IIoPool.Invoke` returning an `IObservable<T>` is enough.

# Anti-patterns — each reintroduces the problems above

| Smell | Why it's wrong | Do instead |
|---|---|---|
| `async Task SendAsync()` awaited from hub/tool/view code | async on a hub thread; no log; can deadlock the reactive scheduler | `RunActivity` + `IIoPool` |
| `new HttpClient(); await http.SendAsync(...)` inline | unbounded, on the subscribing thread, invisible | `pool.Invoke(async ct => …)` inside the command |
| Ad-hoc read from a scoped/ephemeral `cache/…` hub with a manual `.Timeout()` | subscribes with no routable owner → NotFound-storm/timeout | run under a real `partitionPath` |
| A bespoke "control-plane node" per operation, re-implementing status + messages by hand | duplicates what `ActivityLog` already gives uniformly | model it as an Activity; write only *domain* result fields onto your node |
| `partitionPath: ""` (or whitespace) | ownerless `_Activity/{id}` → every poster/subscriber NotFound-storms | pass the real owning partition |

# Candidates in this codebase (migrate toward the pattern)

Side-effects that should be activities: **email send** (`GraphMail` / `GraphEmailSender`), **Stripe checkout**
(`StripeGateway`), **payment-webhook fulfillment** (`PaymentInboxWatcher`), **self-update deployment patch**
(`KubernetesDeploymentUpdater`). Each currently uses `IIoPool` correctly but without the uniform activity
log; bringing them under `RunActivity` gives every one a durable, cancellable, auditable record.

# Related

- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — create → watch → cancel → terminal write.
- [Controlled IO Pooling](/Doc/Architecture/ControlledIoPooling) — the `IIoPool` boundary, the named pools.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why no `async`/`await` in hub-reachable code.
