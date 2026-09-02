---
Name: A create that could not reach the store says so
Category: Fix
Description: When the database is briefly unreachable, creating a node now reports that it was not attempted and can be retried — instead of an "unexpected error" that reads like a refusal and invites a duplicate on the next try.
Icon: DatabaseWarning
Order: -20260902
---

# A create that could not reach the store says so

Databases have bad seconds. When the one behind a deployment briefly stops accepting connections,
every write waiting on a fresh connection times out — and until now, creating a node reported that
as:

> Unexpected error: The operation has timed out

Two different people were misled by that one sentence, in two different directions.

**Whoever looks after the deployment** read "unexpected error during node creation" and went looking
for a bug in the create. There was no bug in the create. A database was unreachable for a few
seconds, and the message named the innocent party — the same wording problem that used to make a
page whose content store was unreachable report itself as a broken page.

**Whoever asked for the create** got an answer that was indistinguishable from a refusal — from
"that name is taken", "you are not allowed", "that type does not exist". Those mean *stop*. This one
means *try again*, and the difference is not cosmetic: something that reads "refused" and then tries
again under a **new** name leaves two copies of the same thing behind.

Both halves are now correct. A create whose store could not be reached is reported as *unavailable,
not refused*, and says all three of the things a caller acts on:

> Node creation at 'Acme/Reports/Monthly' could not be attempted: the data store was unreachable.
> Nothing was written — this is an availability failure, not a refusal, so retrying the same request
> (with the same node id) is meaningful.

The log line names the store rather than the create, and stays at error level, because a database
that cannot be reached is something an operator should see.

**Nothing retries automatically, and that is deliberate.** A short, bounded retry already runs
further down — roughly a second and a half of it — so a failure that gets this far is one where
waiting longer had already been tried and had not helped. Retrying again here would only pile more
attempts onto the machine that is already struggling. The right answer to a spent budget is to say
what happened accurately, and let the caller decide.

A genuine mistake — a query error, a missing table — is untouched and still reported as the
unexpected failure it is. Being told to "come back later" about something that is never coming back
would be the same bug wearing the other face.
