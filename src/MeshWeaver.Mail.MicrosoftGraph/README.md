# MeshWeaver.Mail.MicrosoftGraph

Mail over Microsoft Graph, shipped as a module: system email (`IEmailSender` via `/sendMail`),
inbound mail intake with its change-notification webhook, and the Executive Assistant's mailbox
tools.

## Activating

```json
{
  "Modules": { "Assemblies": [ "MeshWeaver.Mail.MicrosoftGraph.dll" ] },
  "Email": { "Enabled": true, "InboundEnabled": true }
}
```

Without the DLL a deployment sends no mail: `IEmailSender` falls back to the host's no-op, the
invitation and outbound-drain services keep resolving, and `POST /api/email` is simply absent.

## Why it is a module

The Microsoft Graph SDK was the heaviest dependency in the portal image — **43 MB across ten
assemblies** (nine `Microsoft.Graph*`/`Microsoft.Kiota*` plus `Microsoft.IO.RecyclableMemoryStream`) —
for four files of code. It also costs at runtime: `KernelScriptReferences`
documents `Microsoft.Graph.dll` as a **41 MiB native metadata block** in the Roslyn script
reference set, named there as a direct cause of CI memory-pressure flakes. A deployment that sends
no mail should carry neither.

The seam already existed. `IEmailSender` and `EmailOptions` live in the mesh contract, and
`HubEmailExtensions` resolves the sender **optionally** — yielding `false` rather than throwing
when nothing is registered.

## What stayed in the host

Everything mail-shaped that does not touch the SDK: `InvitationEmailSender`, `OutboundEmailSender`
and `NoOpEmailSender` (all on `IEmailSender`), plus `EaGraphAuth` and its OAuth consent controller
— the per-user delegated token flow is raw OAuth, not an SDK call. This module depends only on the
SDK-free `IEaGraphAuth` seam in the mesh contract.

## The two registration details that matter

- **`IEmailSender` resolution is order-independent.** This module registers the Graph sender with
  `AddSingleton`; the host registers its no-op with `TryAddSingleton`. Whichever runs first, the
  last registration wins for `GetRequiredService` and `TryAdd` declines when one already exists —
  so the module always wins when listed, and the two `GetRequiredService<IEmailSender>` call sites
  in the host stay resolvable when it is not.
- **Inbound intake runs on the PORTAL hub**, passed explicitly, because it finds-or-creates
  conversation threads and must be on the hub those threads live on.

## The webhook

`POST /api/email` is mapped **`AllowAnonymous`** on purpose: Graph posts change notifications
unauthenticated and the shared `clientState` secret is the guard. It was an MVC controller in the
host and is a minimal-API endpoint here, because the module endpoint hook maps route handlers
rather than controllers — same route, verb, shapes and status codes.
