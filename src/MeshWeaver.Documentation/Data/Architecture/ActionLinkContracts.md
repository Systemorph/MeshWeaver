---
Name: Action-Link Contracts
Category: Architecture
Description: The neutral, versioned contract a domain module and a link publisher share — an action invocation, the handler that interprets it, the authenticated recipient context — and the durable action reference an activity keeps after its execution hub has unloaded.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.07 0l3-3a5 5 0 0 0-7.07-7.07l-1.5 1.5"/><path d="M14 11a5 5 0 0 0-7.07 0l-3 3a5 5 0 0 0 7.07 7.07l1.5-1.5"/></svg>
---

# Action-Link Contracts

Two things live in the platform so that modules can build shareable, resumable actions without
depending on each other:

- the **action-link contract** — `MeshWeaver.ActionLinks`, in `MeshWeaver.Mesh.Contract/ActionLinks/` — a
  description of something a person can later be offered to do, and the handler that knows what it
  means;
- the **durable activity action reference** — `ActivityActionReference`, stored on
  `ActivityLog.Actions` — the caption and place-to-go an activity still offers after its execution
  hub is gone.

Both are **data contracts only**. The platform ships no link publisher, no route, no QR card, no
coupon and no payment behaviour: those belong to the modules that consume the contract.

## Why the platform carries it

A module that knows a domain (a store's coupon handler) and a module that publishes links and
resumes sign-in must not depend on each other in both directions. Each depends on the neutral
record instead: the domain module **emits** an `ActionInvocation`, the publisher **stores** it, and
the registered `IActionHandler` for its key **interprets** it.

It is in `MeshWeaver.Mesh.Contract` for the reason the
[payment provider contract](../PaymentProviderContract) is: content compiled inside the mesh has to
bind something present in every portal image, whether or not the module that implements it is
mounted.

## The invocation is data, never code

```csharp
var invocation = new ActionInvocation
{
    HandlerKey = "store.coupon-journey",   // an identifier, compared ordinally
    HandlerVersion = 1,                    // the handler's own payload schema version
    Payload = payloadJson,                 // the handler's typed payload, opaque to the publisher
    RequiredCapabilities = [ActionCapabilities.PaymentMethodSetup],
};
```

An invocation names a handler by **key and version** and carries that handler's payload as opaque
JSON. It holds no delegate, no script, no method name, no hub address and no identity to run as. A
person who follows a link can therefore cause exactly what a handler registered on this portal is
written to do, and nothing a publisher typed.

🚨 **It is not a grant.** Holding an invocation, or the link that resolves to one, entitles nobody to
anything. The handler decides from the authenticated recipient, afresh, every time.

## Handlers: explicit by key and version, fail closed

A handler declares an `ActionHandlerCapability` — its key, the payload versions it reads
(`MinVersion`…`MaxVersion`), whether completing the action `Mutates` anything, and the
`Capabilities` it can offer on this portal **now** (computed from the deployment's actual state, not
a static claim).

`ActionHandlerResolution.Resolve(handlers, invocation)` chooses the handler, or answers why not. It
is pure: the same verdict comes from a service container, a list a test builds, or any other
registry.

| Outcome | When |
|---|---|
| `Ready` | exactly one handler reads this key and version and offers every required capability |
| `UnsupportedContract` | the envelope was written by a newer contract than this build reads |
| `UnknownHandler` | nothing is registered under the key — including on a portal that mounts no handler at all |
| `UnsupportedVersion` | a handler has the key but not the version; the detail lists what is supported |
| `AmbiguousHandler` | two handlers claim the same key and version; neither is picked by registration order |
| `MissingCapability` | the handler lacks a required capability; the detail names it |

A publisher asks **before** it publishes, so an advertised link never depends on a module that
merely happens to be loaded, and a recipient who follows an old link gets a named outcome rather
than a blank page. `hub.ResolveActionHandler(invocation)` is the same resolution over the handlers
registered in the mesh's services.

`ActionOutcomeStatus` is an **open vocabulary** of string constants (policy
[`open-vocabulary-string-constants`](../PolicyNotProse)): a handler may answer a word of its own.
`ActionOutcomeStatus.IsSuccess` is true only for `Ready`, `Pending` and `Completed`, so a word this
build has never seen is reported verbatim and treated as **not** successful. The same holds for
`ActionCapabilities`, `ActivityActionKind` and `ActivityActionAvailability`.

## There is no "execute"

`IActionHandler` has two calls, and both are **read-only**:

- `Validate(invocation, publicationContext)` — may this publisher publish this to this site? The
  default answers the structural check; a handler whose domain restricts who may distribute an
  action (the right to hand out a coupon is not the right to run an activity) overrides it.
- `Prepare(invocation, recipientContext)` — for one authenticated recipient, where do they go?
  `Ready` with the handler's own review page, `Completed` with the existing result, or a refusal.

`Prepare` is what a scan, a refresh, a link preview and a sign-in callback reach, any number of
times. It grants nothing, reserves nothing, saves nothing and charges nothing.

Whatever the action then changes is changed on the handler's own typed node, through the mesh's one
mutation API ([`GetMeshNodeStream(path).Update(…)`](../RequestViaStreamUpdate)), by the
authenticated person confirming on that node's page, under the node type's own access rules.
Nothing in the contract can be made to run a script, pick a hub or act as another identity, because
nothing in the contract mutates.

## Who the recipient is

`ActionRecipientContext` carries the facts a handler is given when someone arrives at a link.

🚨 **`Recipient` is the authenticated principal, and only that.** The composer sets it from the
request's access context after sign-in. It is never taken from the link, the invocation payload, a
prefill, a query string or a posted form.

`Admission(boundTo)` answers, in this order:

1. `Unauthenticated` — nobody real is signed in (blank, `Anonymous`, the `Public` grant subject or
   the system identity). Checked **first**, so an anonymous visitor learns nothing about the link —
   not even that it belongs to another site. The caller sends them to sign in.
2. `OriginMismatch` — the action is bound to a different site than the one serving the request.
3. `Ready`.

## Origin binding

An action is bound to an `ActionSite`: a registry identity plus one canonical origin
(`https://portal.example.com` — `https` only, `http` for a loopback host; no path, query, fragment
or user-info; lower-case; default port dropped).

🚨 **The origin comes from configuration, never from the request.** A site is composed from the site
registration — not from a `Host` header, a forwarded header or a query string, which are the inputs
an open redirect is made of. `IsBoundTo` requires the same site identity **and** the same canonical
origin, both ordinal.

## Idempotency

`ActionIdempotency.Key(parts…)` is the lower-case hex SHA-256 of the parts, each prefixed with its
UTF-8 length, so two different part lists cannot share a key. The same parts give the same key on
every process and after every restart, which is what lets a retry find the work an earlier attempt
started. The algorithm is contract: keys are stored.

- A **publication** is idempotent for (site, publisher, publisher's request id):
  `ActionPublicationContext.IdempotencyKey`. Repeating the request returns the existing link.
- A **recipient's work** is idempotent for (site, recipient, scope):
  `ActionRecipientContext.IdempotencyKey(scope)`. The scope is the handler's choice of what must be
  unique. The link id is deliberately **not** part of the key, so printing a second link to the
  same offer cannot give the same person a second claim.

## The durable activity action reference

A control a script returns reaches a reader by one route: the kernel publishes it into the execution
hub's live area dictionary. That dictionary is empty again once the hub unloads, and the JSON left
on `ActivityLog.ReturnValue` is hollow for a container control. A historical activity therefore
cannot reopen what it produced.

`ActivityActionReference` is the part that must outlive the run:

| Field | Meaning |
|---|---|
| `Id` | identifies the action within its activity; recording the same id again **replaces** the entry |
| `Kind` | `Navigate`, or a module's own word |
| `Label` / `LabelKey` | the English caption and the catalog key that renders it in the viewer's language at render time |
| `Target` | a path on **this** site, beginning with exactly one `/` |
| `ResultPath` | the domain node holding what the activity produced — the reference points at the result, it never duplicates it |
| `HandlerKey` / `HandlerVersion` | the handler the target belongs to, so a renderer can say *unavailable* when it is not installed |
| `ContractVersion` | a reader refuses a higher one |

Every member is a string or a number. **No UI object graph is ever stored**; a renderer builds its
control from these fields on each render.

```csharp
hub.RecordActivityAction(activityPath, new ActivityActionReference
{
    Id = "open-card",
    Label = "Open QR card",
    LabelKey = "actionLink.openCard",
    Target = "/a/6f1c2d3e/card",
}).Subscribe(_ => { }, ex => logger.LogWarning(ex, "Recording the action failed for {Path}", activityPath));
```

`RecordActivityAction` writes through the canonical activity write (`ActivityLogAppender.Append`),
so it shares one writer discipline with the transcript: the terminal status write of the same run
patches only the fields it owns and leaves `Actions` standing. The observable is **cold** — the
write runs on `Subscribe`.

### It is an address, never a grant

The reference carries no credential, no entitlement and no personal data. `Availability()` is the
**structural** verdict — `Available`, `UnsupportedVersion`, `UnsupportedKind` or `UnsafeTarget` — and
reads nothing but the reference, so it answers identically in the run that wrote it and after any
restart. `Available` means *may be offered*: the surface that opens the target still re-checks
authorization and decides `Revoked`, `Expired`, `Unauthorized` and `Unavailable`.

### A target cannot leave the site

`IsLocalTarget` accepts a string that starts with exactly one `/`. It refuses an absolute or
scheme-relative URL, a backslash anywhere (browsers read `/\host` as `//host`), whitespace and
control characters, anything over 2,048 characters, and any of those hidden behind one level of
percent-encoding (`/%2Fhost`, `/%5Chost`, `%0d%0a`). `RecordActivityAction` refuses to **persist**
a reference that is not structurally available, so a bad target never waits in storage for a later
reader.

### Old activities

`ActivityLog.Actions` defaults to empty. An activity written before the field existed reads with no
actions and renders exactly as it did; `ReturnValue` and the live result rendering are unchanged.

## Compatibility rules for this contract

- Every contract record uses init-only properties, never a positional constructor, so a new optional
  field is additive for a module compiled against an earlier build.
- A reader tolerates a property it does not know and **refuses** a `ContractVersion` higher than its
  own.
- The JSON property names are wire contract: stored links and stored activities carry them.
  `ActionLinkContractTest` pins the version-1 forms as hand-written literals.
- A status, kind or capability word is never validated against the platform's own constants.

## What is deliberately not here

| Not in the platform | Where it belongs |
|---|---|
| Publishing a link, the GUID route, continuation through sign-in, the QR card | the action-link module that consumes this contract |
| Coupon, plan and claim semantics | the store module's handler |
| Payment-method setup and renewal | the [payment contract](../PaymentProviderContract) and its provider module |
| Running a script or an arbitrary privileged operation from a link | nowhere — the contract cannot express it |
