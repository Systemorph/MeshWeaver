---
Name: A refused write says when the refusal is doubtful
Category: Fix
Description: When a write is refused but the stored record shows you were given access to that space, the message now says so — instead of the flat "Access denied" that made a momentary glitch look like a settled decision.
Icon: ShieldError
Order: -20260913
---

# A refused write says when the refusal is doubtful

"Access denied" is a sentence people act on. They ask an administrator for access they already have,
or conclude the space is not theirs, or stop.

Almost always it is right. Occasionally it is not: the record of who may do what is kept in the
space itself, and the check that reads it works from a recent copy. When something was granted a
moment earlier — the instant a space is created, or created again under a name it once had — the
check can answer from a copy taken just before the grant, and refuse someone who was already given
the right. Retry and it works. Nothing tells you that, so the first answer is the one you believe.

Now, when a write is refused, the refusal also looks at the stored record directly. If the space's
own record shows a grant under your name that the check did not honour, the message says exactly
that, and says what it can and cannot distinguish:

> 'acme/_Access' holds a node at 'acme/_Access/you_Access' — the path a grant for 'you' is minted
> under — that this permission check did not honour. The durable store and the permission fold
> disagree, so this denial is not a settled decision about your access. Two known causes: the fold
> answered from a snapshot older than the write that granted it (retrying the same operation
> normally succeeds), or that node is not a readable AccessAssignment (its content degraded, so the
> fold skips it and retrying will not help).

Two things it deliberately does not do.

It does not let you through. What it found is evidence that the refusal is doubtful, not permission
to proceed — the record it read is a path, not a decision, and honouring it would be a second way
into your data that nobody reviewed.

And it does not claim more than it saw. The two causes look identical from where it stands, and one
of them is cured by retrying while the other is not. Naming both is the difference between a message
that helps and a message that sends you to try something that cannot work.

An ordinary refusal — for someone the space never granted anything — reads exactly as it always did.
