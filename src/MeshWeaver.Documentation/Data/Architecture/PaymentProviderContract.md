---
Name: The Payment Provider Contract
Category: Architecture
Description: Taking money is an optional module, so the commerce content binds IPaymentProvider — a platform abstraction — and answers "no provider installed" as a modelled state. Why a module namespace in mesh-compiled source breaks every consumer that does not mount it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 10h20"/><path d="M6 15h4"/></svg>
---

# The Payment Provider Contract

🚨 **Card payments are an optional module. The commerce content binds an abstraction the platform
always ships — [`IPaymentProvider`](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Mesh.Contract/Payments/IPaymentProvider.cs) —
and a portal with no payments module mounted must compile, install and run, with the payment
features absent or inert rather than broken.**

The same shape as `IEmailSender`, and for the same reason: a provider-neutral contract in
`MeshWeaver.Mesh.Contract`, the concrete implementation in a module that a deployment may or may not
mount.

## Why this is not a style preference

A `NodeType`'s C# is stored **as data** and compiled by the mesh at runtime
([NodeType Compilation](../NodeTypeCompilation)) against the portal image's reference set **plus the
module assemblies that deployment actually mounts**. A `using` of a module namespace in that source
is therefore not a compile-time dependency the build can check — it is a **runtime requirement on
every consumer's module set**, and `dotnet build` is blind to it.

When the Store's `Store/Catalog`, `Store/Order`, `Store/Plugin` and `Store/Maintenance` sources took
`using MeshWeaver.Payments.Stripe;`, every deployment that did not mount that module got both halves
of the failure at once:

```
Prebuilt assembly for Store/Catalog DECLINED: dependency record mismatch —
  'MeshWeaver.Payments.Stripe' built against mvid:ac387a93…, live is absent — compiling instead
Compilation failed for 'Store/Catalog': CS0234 — the type or namespace name 'Payments'
  does not exist in the namespace 'MeshWeaver'
```

The prebuilt assembly is declined *and* the local fallback compile fails, because the namespace
genuinely is not there. The fallback exists to survive a version skew; it cannot conjure an absent
assembly. Every instance of those node types then reads back with an empty `content`, so pages that
have nothing to do with money render blank.

**Nothing in a deployment's own configuration can fix that.** Its only escape is to mount the
payment module — which is the opposite of optional, and makes an outbound-call surface to a card
processor mandatory on portals that sell nothing.

## The contract

`MeshWeaver.Mesh.Contract/Payments/` carries the whole seam, and nothing in it names a processor:

| Member | What it answers |
|---|---|
| `Sells` | does this portal take money at all? |
| `Unavailable` | the reason a payment cannot be taken right now, in the viewer's language — `null` when one can |
| `CreateCheckout(PaymentCheckoutRequest)` | open a hosted checkout; `Recurrence` non-null makes it recurring |
| `CancelAtPeriodEnd(subscriptionId)` | stop renewing at the end of the paid period, never immediately |
| `ReadDelivery(signature, body, now)` | verify a webhook delivery **and** parse it, in one answer |
| `DescribeDeliveryPath(hookUrl)` | can the processor actually reach this portal? |
| `SecretSettingName` / `WebhookSecretSettingName` / `DeliverySignatureHeader` | the names an operator finding has to quote |

`PaymentMetadata` holds the keys a checkout stamps and the delivery reader looks for
(`orderPath`, `pluginPath`, `buyer`, `couponCode`, `planTier`, `cadence`). They are **on the wire**
at the processor: a session created before a rename hands the old key back weeks later, so they are
durable contract exactly like a configuration key — and they live in the platform because the
mesh-compiled content that stamps them must bind something present in every image.

`PaymentSettings.BaseUrlConfig` (`Commerce:BaseUrl`) is the portal's own public URL. It is
provider-neutral on purpose: a deployment states it once, and swapping providers does not restate
it.

### Verify and parse come back together

`ReadDelivery` returns a `PaymentDelivery` carrying **both** `Authentic` and what the delivery is
about. That is not a convenience — it is the only shape in which a caller can say *"this did not
authenticate AND it names a paid order"*, which is the distinction that keeps **someone paid and was
not served** from looking exactly like **the processor never called**
([Plugins#1069](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1069)). An unauthenticated
delivery that names consequential work is retained as evidence and replayed once the secret is
fixed; junk is discarded so nobody can fill the container and drown the signal.

### A read that failed is not an empty account

`DescribeDeliveryPath` never faults. An unreachable processor, a refused credential, a truncated
listing and an unparseable body all produce "nothing found", and collapsing them into "there is no
endpoint" is what left a portal taking money and fulfilling nothing for days
([Plugins#1109](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1109)). So `ReadProblem` and
`Truncated` are carried separately from `Endpoints`, and a caller that cannot tell must say so
rather than report health.

## Absence is a modelled state

`hub.PaymentProvider()` returns `IPaymentProvider?`. The null **is the design**: it makes "no
payments module mounted" a state a caller answers in code, instead of a missing type a caller cannot
compile against.

🚨 **These three are forbidden**, and each was ruled out explicitly:

- a `try`/`catch` around a payment type — the failure is at compile time, not at call time, so
  there is nothing to catch;
- a reflection probe for the module assembly — it re-creates the runtime coupling the abstraction
  removes, and answers differently on a deployment that mounts the module *later*;
- a `null` provider held and dereferenced at first use — that is not degradation, it is a
  `NullReferenceException` on the payment path, which is worse than the compile error it replaced.

The correct shape is the same everywhere: resolve, ask, and have a sentence ready for "no provider".

```csharp
// The reason this portal cannot take a payment, or null when it can.
public static string? PaymentsUnavailable(IMessageHub hub) =>
    hub.PaymentProvider() is { } provider
        ? provider.Unavailable
        : Texts(hub).NoPaymentProvider;
```

A portal with no provider therefore *refuses* a checkout with a sentence, reports its payment path
as **out of scope** rather than healthy or broken, and leaves an unverifiable delivery in place
instead of discarding it — all states the commerce surfaces already had for an unconfigured
provider.

## Everything reactive, everything cold

Nothing on the contract returns a `Task`. Every method returns `IObservable<T>` and the side effect
runs on `Subscribe`, never on the call ([Asynchronous Calls](../AsynchronousCalls)). An
implementation bridges its HTTP leaf through the mesh's bounded `IIoPool`
([Controlled IO Pooling](../ControlledIoPooling)) and never `Observable.FromAsync`, which would run
the prologue on the subscribing thread and bound nothing.

## Landing an abstraction like this across the fleet

🚨 **The contract has to be IN THE IMAGE before any consumer can bind it, and a dependent repo's
compile gate proves that against the image rather than against core's source.** MeshWeaver.Plugins'
`Compile every NodeType (vs core)` takes its whole reference set from the pinned platform image —

```yaml
- name: Take the framework assemblies from the platform image
  run: docker cp "$id:/app/." refs/          # MW_IMAGE_DIGEST
```

— so `MW_PLATFORM_REF` (which core commit is *built and tested*) does not move it. A pinned digest is
immutable and no CD run changes it. The consequence is a strict order, and skipping a step reads as a
defect in the content rather than as a missing pin:

1. the contract merges to core `main`;
2. core's Continuous Delivery seals a platform set that contains it;
3. the dependent repo moves its pins as **one gate** — `MW_PLATFORM_SET`, `MW_IMAGE_DIGEST`,
   `MW_PORTAL_IMAGE_DIGEST`, `MW_PLATFORM_REF`, every `uses:` ref and every
   `platform-image-digest:` / `tester-image-digest:` literal
   (`check-platform-pins.py --check-tags` is what refuses a half-move);
4. only then does the content that binds the contract compile there.

Between (1) and (3) the dependent's PR is **structurally red**, which is the mechanism working. Hold
it as a draft — draft is the opt-out from auto-arm, so it cannot enqueue while a required gate cannot
pass — and say in the body which of the four steps it is waiting on.

The verdict that *can* be had immediately is the one that matters most, and it needs no image at all:
compile the dependent's node types against a **core-only** reference set
(`compile-check.py --refs <core>/src`). That set is precisely "a mesh with no optional modules", so
it answers the acceptance question — does this content compile where the module is absent? — before
any pin moves.

## Adding a second provider

Implement `IPaymentProvider` in a new module assembly, register it as a singleton from the module's
`MeshNodeProviderAttribute`, and ship it as its own package. Nothing in the commerce content
changes, and no deployment that does not mount it is affected — which is the property this contract
exists to buy.

The one thing a new provider owns beyond the calls is its **vocabulary**: the events its delivery
endpoint must be subscribed to, the word it uses for an enabled endpoint, and the remedy an operator
follows. Those ride on `PaymentDeliveryPath` rather than being hard-coded by the caller, so an
operator health board written once reads correctly for any provider.
