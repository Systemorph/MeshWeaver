---
Name: Account Lockout
Category: Architecture
Description: How a platform administrator locks a person out of an instance — sign-in, tokens, open sessions and agent runs refused, no effective permission anywhere — without touching the person's data, and how an unlock gives the account back unchanged. Where the record lives, the four places it is enforced, and the guards that keep a lock from removing an administrator.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>
---

# Account Lockout

When a person must no longer use an instance — a consultant whose engagement ended, an employee who
left — the platform administrator **locks the account**. A lock is not a deletion: the person's home
partition, their threads, their contributions elsewhere and their grants stay exactly where they are,
so an **unlock** gives the account back as it was. Removing data is a separate, deliberate step that
a lock never takes.

## The record

| | |
|---|---|
| **Path** | `Admin/_AccountLock/{userId}` — one node per person, node type `AccountLock` |
| **Content** | `UserId`, `LockedAt`, `LockedBy`, `Reason`, `UnlockedAt`, `UnlockedBy` |
| **In force** | while `LockedAt` is set and `UnlockedAt` is not |
| **Register** | `search path:Admin/_AccountLock scope:descendants nodeType:AccountLock` |

The record lives in the **Admin partition**, not on the person's `User` node. The User node is the
root of the person's own partition, where they hold Admin — a lock there would sit inside the reach
of the person it locks out, and a platform administrator has no standing access to it (a global admin
is not a data superuser, see [Access Control](../AccessControl)). The Admin partition is the one
partition only a platform administrator can write; invitations, service identities and declared grant
rules live there for the same reason.

An unlock **keeps** the record and stamps `UnlockedAt`/`UnlockedBy`, so the version history of that one
node answers the operator's question — who locked, when, why, and who gave the account back.

## Locking and unlocking

`AccountLocks.Lock(hub, caller, userId, reason)` and `AccountLocks.Unlock(hub, caller, userId)` follow
policy `authorize-as-caller-execute-as-system` ([Authorize as the caller, execute as System](../AuthorizeAsCallerExecuteAsSystem)):
the caller is passed explicitly, must be a platform administrator (`hub.IsGlobalAdmin(caller)`), and
only then is the record written as System. The portal's **Accounts** tab under Admin → Settings is the
human surface for both verbs (MeshWeaver.Plugins).

A lock is refused, by name, when it would:

1. name something that is not a person — a reserved principal (`System`, `Anonymous`, `Public`, a hub),
   a service principal (those are *revoked*, see [Service Identities](../ServiceIdentities)), an email or
   a path;
2. lock the caller themselves;
3. lock a **platform administrator**. Another administrator removes their `Admin/_Access` grant first.
   This is what keeps a lock from ever leaving an instance without a human administrator, which would
   hand the next sign-in the first-user bootstrap.

The same rules hold for a record written any other way (an MCP `create`/`update`/`delete`, an import,
an upsert that retypes a node into a lock): the `AccountLockWriteGuard` validator accepts a lock write
only from a platform administrator or from System executing a verb that already checked one (an Editor
grant on the Admin partition is not enough), and refuses a lock in force that names an administrator
or its own writer, and a record whose node id is not the user it names.

🚨 **A lock never applies to a platform administrator, however its record was written.** This is held
at READ time, not only by the validator: the fold asks whether a locked subject holds `Permission.All`
on the Admin partition (evaluated without the lock) and, if so, ignores the lock. Every other seam reads
the same effective verdict (`AccountLocks.ObserveLocked`). So no write path — one a validator does not
see included — can lock the last administrator out.

Account ids are compared **case-insensitively** (`AccountLocks.IdComparer`), as home ownership and the
user directory compare them: a lock written as `JDOE` locks `jdoe`, and an unlock finds it.

Unlocking an account that is not locked writes nothing. The register only discovers whether a record
exists; whether it is still in force is decided inside the update, on the owner's authoritative node, so
a lift another replica already wrote is not stamped twice.

## Where it is enforced

All four seams read the **same** live register (`AccountLocks.ObserveAll`, one shared, Admin-anchored
read per process), so they never disagree about who is locked.

| Seam | What a locked person gets |
|---|---|
| **The permission fold** (`PermissionEvaluator`) | `Permission.None` on every path — their home, every space, every public surface. Every node read, write, delivery and agent tool call passes this fold, so an open Blazor session and an agent run on the person's behalf are refused at their next check. |
| **The RLS node validator** (`RlsNodeValidator`) | Refused before the own-home shortcut and the per-type rules, which answer without asking the fold. |
| **Hub-level rules** (`HubPermissionRuleSet`, the delivery gate) | A hub rule such as the User hub's "every signed-in person reads" is taken on the spot only on a determined *unlocked* verdict; otherwise the check is held and decided as the rule's grant unless locked. A subscription admitted by such a rule follows the lock and ends when one applies. |
| **Declared point reads** (`DeclaredPointRead`) | The live standard read, the declaration's admission and each revision check answer *denied* for a locked person — live, so a read open when the lock lands is withdrawn on the same subscription. |
| **Node-type access rules** (`NodeTypeAccessRuleGate.EvaluateUnlessLocked`) | Denied before the rule runs. A rule may grant where the fold does not — a profile readable by every signed-in user, a submitter reading their own submission — and a locked account holds nothing. Declared point reads are refused the same way. |
| **The query boundary** (`MeshService`) | One empty answer, read from no partition. A LIVE query is gated for its whole lifetime: the moment a lock applies, the provider's subscription is released and a `Reset` read from no partition clears every row the subscriber held; an unlock re-subscribes it. The SQL-backed providers filter rows in the database and never consult the fold, so this is what keeps a locked person's listing empty there. |
| **The portal's admission** (request middleware, MeshWeaver.Plugins) | A 403 at sign-in and for every bearer token, including MCP and API tokens. |

The fold's lock leg is **not seeded**: a lock subtracts everything, so an optimistic "unlocked" before
the register has answered would be an admission window (the monotonicity rule in
[Access Control](../AccessControl) → "The convergence contract"). **"Not known yet" is never "not
locked"** anywhere: the synchronous view (`AccountLockCatalog.IsLocked`) answers *locked*, *not locked*
or *not known yet* from an immutable snapshot; the query boundary waits for a verdict within the
permission-establishment budget and otherwise fails the read as unavailable; the admission waits and
answers 503 + Retry-After; and a register read that faults reaches every waiter as a fault, never as
silence.

## What a lock does not do

- It deletes nothing and moves nothing. Data the person created anywhere stays, attributed to them.
- It does not revoke their grants or group memberships; they simply confer nothing while the lock is in
  force, and confer them again after an unlock.
- It does not stop sign-in at the identity provider. On an invitation-only instance the platform's
  refusal is enough; an operator who also wants the identity provider to refuse disables the account
  there.
- It does not reach a person's data on **another** instance.
