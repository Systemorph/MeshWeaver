---
nodeType: Markdown
name: Adding a Data Sync Needs a Global Admin
category: Architecture
description: Any change that adds or widens the synchronisation of data — a new mirror, a new GitSync, a new replica target, a one-way link made two-way — is approved by a global admin through a governed activity. A sync is a standing grant, not a one-off action.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0f766e'/><path d='M6 9h9l-2.5-2.5M18 15H9l2.5 2.5' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/><circle cx='18' cy='7' r='2.6' fill='#fcd34d'/></svg>"
---

# Adding a Data Sync Needs a Global Admin

> **Any change that ADDS or WIDENS the synchronisation of data is approved by a global admin,
> through a governed activity. Not a pull request comment, not a maintainer's nod in chat — a
> signature on a `Governance/Activity`.** Policy `data-sync-approval`.

## Why a sync is different from other changes

Most changes do a thing once. **A sync is a standing grant**: once it exists it keeps moving data,
on its own, indefinitely, under an identity nobody re-examines. The change that created it is
reviewed once; the data movement it authorises is reviewed never.

That asymmetry is the whole argument. A one-off export is bounded by the person who ran it and the
moment they ran it. A mirror configured on a Tuesday is still copying rows a year later, into a
place chosen by someone who may have left, at a scope nobody has re-read since. The review that
matters is therefore the one at creation, because there will not be another.

Three properties make it worse than it first looks:

- **It is invisible once it works.** A healthy sync produces no events. Nobody is prompted to ask
  whether it should still exist.
- **Scope creeps without a code change.** Adding a partition to an existing sync's path, or
  renaming something into its scope, widens what moves with no diff that looks like a data change.
- **Direction is load-bearing and easy to flip.** A one-way link made two-way is one configuration
  value, and it converts a publication into a channel that can write back.

## What counts — the trigger list

Approval is required to:

- **add a mirror** between instances, in either direction
- **add a GitSync** on a partition, or point an existing one at a different repository or branch
- **add a replica target** — a warehouse, a reporting store, an external system
- **widen an existing sync's scope** — another partition, another namespace, a broader path
- **change a sync's direction** — one-way to bi-directional is always a new approval, never an edit
- **change the identity a sync runs as**, or what it may write

If a change makes data appear somewhere it did not appear before, it is on this list, whatever the
diff looks like.

## What does NOT need it

The rule is about standing grants, not about reading:

- reading data you already have access to, however much of it
- a one-off export or report a person could have produced by hand, bounded and attributable
- a sync that already exists continuing to run — unchanged
- moving content **within** one instance, where no new boundary is crossed

Keeping this list honest matters. A rule that also catches ordinary reads gets routed around, and
then it catches nothing.

## How approval is obtained

Through a governed activity, because that is the mechanism that already carries the properties this
needs: the approval is **bound to a content hash**, so changing the scope after approval lapses the
signature; the signer must be a **global admin**; the **proposer may not sign**; and the whole thing
leaves an auditable trail naming what was approved and when.

The proposer does not need — and must not be given — the rights to create the sync. That is the
point of proposing: *"you want something DONE that you are not allowed to do yourself. You do not do
it, and you do not ask for a credential."*

> 🚨 **A credential request instead of a proposal is the anti-pattern.** If a change needs a sync
> that the person or agent cannot create, the answer is a proposal, never a temporary grant. A grant
> issued to get past this rule outlives the change exactly as the sync does, and now there are two
> standing grants where there should have been one reviewed decision.

## For review

A diff that configures synchronisation without a referenced approval is a finding, and it is one of
the few that **does** block — unlike most findings, which are filed and carried past. The reason for
the difference is the same asymmetry as above: a defect that ships can be fixed afterwards, whereas
data that has been copied somewhere cannot be un-copied.

See [Policy Not Prose](../PolicyNotProse) for the register, and
[Access Control Architecture](../AccessControl) for what a grant is and is not.
