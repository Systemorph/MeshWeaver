---
Name: Payments are something a portal can leave out
Category: Feature
Description: The platform now carries a provider-neutral payment contract, so commerce content binds an abstraction instead of a card processor and a deployment that mounts no payments module still works.
Icon: PlugConnected
Order: -20260906
---

Taking card payments is an optional module, but the Store's node types named one directly. A
deployment that did not mount it could not compile them at all — `CS0234: the type or namespace name
'Payments' does not exist in the namespace 'MeshWeaver'` — and pages with nothing to do with money
rendered blank.

The platform now ships `IPaymentProvider` in `MeshWeaver.Mesh.Contract`: a provider-neutral contract
for opening a hosted checkout, cancelling a subscription at the end of its paid period, verifying and
reading a webhook delivery, and asking whether the processor can actually reach this portal. A
payment module supplies an implementation; a portal that mounts none simply resolves nothing.

**The absence is a state, not a null to guard.** `hub.PaymentProvider()` returns `null` on a portal
that does not sell, and every commerce surface answers that case explicitly — a refusal with a
sentence in the viewer's language, a payment-path report that says *out of scope* rather than healthy
or broken. No reflection probe for an assembly, no `try`/`catch` around a payment type.

The reasoning, and the shape a second provider implements, is in
[The Payment Provider Contract](/Doc/Architecture/PaymentProviderContract).
