---
Name: Node content now reads typed on every hub
Category: Fix
Description: Seven built-in node types declared a content type that only their own hub knew about, so a reader anywhere else in the mesh got raw JSON and a silent null — inbound mail on one install was dead with no error. All seven are registered, and a guard now fails the build if an eighth is added.
Icon: DocumentBulletListMultiple
Order: -20260830
---

# Node content now reads typed on every hub

A node type declares what its content **is** — `Invitation`, `NotificationRule`,
`GraphSubscriptionState` and so on. That declaration configured the node's own hub and nothing
else, so any *other* hub that read the node had no `$type` discriminator for the value and received
an untyped `JsonElement` instead of the record.

That failure is completely silent. There is no exception and no error line: consumers written as
`content is Invitation` simply see `null`, the view renders empty, and a reactive wait times out
with nothing to point at.

It was found on `memex.systemorph.com`, where the portal logged at boot that
`Admin/_GraphSubscription/inbox` *"stayed an untyped JsonElement after deserialization"*. That node
carries the state of the inbound-mail subscription, so the renewal could not see its own record and
inbound mail on that install was dead — with every health signal green.

Seven built-in node types were in that position: `EaCredential`, `GraphSubscriptionState`,
`Invitation`, `MemexClientContent`, `NotificationChannel`, `NotificationRule` and
`TeamsConversation`. All seven now register their discriminator mesh-wide.

The lasting part is the guard. The two halves of this contract — the declaration, inside a hub
configuration lambda, and the registration, a separate statement in the enclosing method — were
joined by nothing, and omitting the second compiles, boots, serves and passes every test. A new
governance test now reads both halves out of the source and fails when a node type declares a
content type it never registers, naming the type and the three accepted spellings of the fix.
