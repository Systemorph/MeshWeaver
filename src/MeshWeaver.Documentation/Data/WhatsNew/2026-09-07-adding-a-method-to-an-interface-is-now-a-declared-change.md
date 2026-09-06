---
Name: Adding a method to an interface is now a declared change
Category: Feature
Description: A forwarder keeps everyone who CALLS your interface compiling. It does nothing for everyone who IMPLEMENTS it — and until now nothing in the platform's checks noticed the difference, so the breakage arrived days later in somebody else's repository.
Icon: ShieldCheckmark
Order: -20260907
---

# Adding a method to an interface is now a declared change

Take away a public type or a public method, and the platform's checks stop you until you say where
its counterpart landed. That has been true for a while, and it is what keeps a change that spans two
repositories from breaking the second one.

Adding was assumed to be safe. For anyone who *calls* your code, it is. For anyone who
**implements** your interface, it is not: a new member on an interface is a member they now have to
write, and their build stops with

```
error CS0535: 'FakeEaGraphAuth' does not implement interface member 'IEaGraphAuth.ExchangeAndStore(…)'
```

The usual courtesy — leaving the old members behind as forwarders so nothing breaks — does not help
here at all. **A forwarder rescues a caller. It cannot rescue an implementer.**

## Why this was expensive rather than merely annoying

Nothing noticed at the time. The change that added three members to one interface passed every
check, reviewed clean and merged; the breakage appeared days later, in a different repository, in a
pull request whose own diff was three version numbers. And by then the two halves could only be
fixed together: the pin bump could not build without the adaptation, and the adaptation could not
build without the pin. That repository's main branch was unusable for hours.

## What happens now

A pull request that adds a member to a public interface with no default implementation — or an
`abstract` member to a public abstract class — is asked to say so, naming each member and what was
checked:

```
Implementers: IFoo.Bar — who implements this and where their update lands
```

Two things about that are deliberate.

**Giving the member a default implementation makes the whole question go away**, and the check goes
quiet on its own. That is the real fix whenever it is available: a default implementation is exactly
what keeps every existing implementer compiling.

**It asks you to predict the coupling, not to wait for it.** Removing something means the deleting
half lands last, so that check waits for a merged counterpart. Adding is the other way round — the
other repository cannot even compile its update until this change has shipped — so waiting for a
counterpart would have demanded the very deadlock that made the original incident expensive.

## How often you will meet it

Almost never. Across the last hundred changes to the platform's public surface, exactly one would
have been asked to declare anything: the one that caused the incident.
