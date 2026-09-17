---
Name: A CRM client can no longer be created inside another space
Category: Fix
Description: An instance of a package type that owns its own partition — a CRM client — is refused below the top level, exactly as a nested Space always was, and the Create form now places one at the top level itself.
Icon: ShieldCheckmark
Order: -20260917
---

# A CRM client can no longer be created inside another space

Some types own a partition of their own: a Space, a User, and types a package brings, such as a CRM
client. An instance of such a type is the root of that partition, so it only makes sense at the top
level.

For the platform's own types this was always enforced. For a type that comes from a package it was
not: creating a CRM client *inside* a space went through, and produced a client with no partition of
its own — something the type says cannot exist. That create is now refused, with a message in your
language explaining that the type must be created at the top level.

Checking this does not slow ordinary content down or make it fail intermittently: the answer is read
from the type's stored definition, never by starting the type up. If that definition cannot be read
at that moment, the create is refused as temporarily unavailable and can simply be retried.

The Create form now knows which package types own their partition too, so choosing one shows the
namespace as the top level and creates it there, instead of leading you into the refusal.
