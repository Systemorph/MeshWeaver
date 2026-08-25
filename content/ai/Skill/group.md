---
nodeType: Skill
name: /group
description: Create an access-control Group and put people in it — the group node, granting the group access to a space (one AccessAssignment whose subject is the group), adding members who already exist, and inviting members who don't yet (an email invite that lands group membership the moment they sign in, via the durable AddToGroup event subscription). Covers placement (one partition), the exact node shapes, and verification.
icon: People
category: Skills
order: 12
---

You are creating an **access-control Group** and populating it. A Group is a platform-shipped
`AccessObject` node; membership + access are **data** (like [/access](/access)): you grant the *group*
access once, then every member inherits it. Membership resolves through the permission matview's
group-expansion — so **the group, its memberships, and its space-grant must all live under the SAME
partition** (the matview resolves group access within a schema). Put the group *under the space* it serves.

# The model — grant the group once, add members forever

```
{Space}/{Group}                              ← the Group node (nodeType: Group)
{Space}/{Group}/{member}_Membership          ← one GroupMembership per member (a CHILD of the group)
{Space}/_Access/{Group}_Access               ← ONE AccessAssignment: accessObject = the group PATH
```

- **The group's `accessObject` in the grant is the group's PATH** (`{Space}/{Group}`), and each
  membership's `groups[].group` is that **same path**. They must match exactly — that string is the join key.
- **`member` is the userId** — the User node's id (its partition-root id, e.g. `amaier`), the same value
  `_Access` grants use as `accessObject`. NOT the email, NOT a display name.
- Everything under `{Space}` so it lands in the space's Postgres schema. A group in another partition
  will not resolve access to this space.

# Recipe 1 — create the group (under the space it serves)

```bash
mcp create --node '{
  "id": "Team", "namespace": "YouTube", "name": "YouTube Team",
  "nodeType": "Group",
  "content": { "$type": "AccessObject", "description": "Collaborators on the YouTube space" }
}'
```

# Recipe 2 — grant the group access to the space (do this ONCE)

An `AccessAssignment` at `{Space}/_Access` whose **subject is the group path**. Members inherit this.

```bash
mcp create --node '{
  "id": "Team_Access", "namespace": "YouTube/_Access", "name": "YouTube Team — Editor",
  "nodeType": "AccessAssignment", "mainNode": "YouTube",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "YouTube/Team", "displayName": "YouTube Team",
    "roles": [ { "$type": "RoleAssignment", "role": "Editor" } ]
  }
}'
```

`mainNode` MUST equal the scope (`YouTube`) — an empty `mainNode` is silently ignored (see [/access](/access)).
Role is `Admin` / `Editor` / `Viewer` / `Commenter` or a custom `Role` id.

# Recipe 3 — add a member who ALREADY has an account

A `GroupMembership` node as a **child of the group**, id `{userId}_Membership`, `groups[].group` = the group path:

```bash
mcp create --node '{
  "id": "amaier_Membership", "namespace": "YouTube/Team", "name": "amaier — Team",
  "nodeType": "GroupMembership",
  "content": {
    "$type": "GroupMembership",
    "member": "amaier", "displayName": "amaier",
    "groups": [ { "$type": "MembershipEntry", "group": "YouTube/Team" } ]
  }
}'
```

The creator/owner adds themselves this way. For any other person who has logged in before, use their
exact userId (their partition-root id — find it with `search nodeType:User content.email:{email}`).

# Recipe 4 — invite someone who does NOT have an account yet (durable onboarding)

Two nodes, both in the **Admin** partition. The `Invitation` triggers the email; the `EventSubscription`
(an `AddToGroup` continuation) adds them to the group the moment their `User` node is created — surviving
restarts (the `EventSubscriptionRunner` reconciles on boot). Both are idempotent by deterministic id.

```bash
# (a) the invitation — InvitationEmailSender emails any Pending invitation (see caveat below)
mcp create --node '{
  "id": "newcomer_example_com", "namespace": "Admin/Invitation", "name": "Invitation newcomer@example.com",
  "nodeType": "Invitation",
  "content": { "$type": "Invitation", "email": "newcomer@example.com", "invitedBy": "amaier",
               "note": "Invited to group YouTube/Team" }
}'

# (b) the durable subscriber — add to the group on sign-up. Id: addgroup_{slug(email)}_{slug(groupPath)}
mcp create --node '{
  "id": "addgroup_newcomer_example_com_youtube_team", "namespace": "Admin/EventSubscription",
  "name": "NodeChange → AddToGroup", "nodeType": "EventSubscription",
  "content": {
    "$type": "EventSubscription",
    "id": "addgroup_newcomer_example_com_youtube_team",
    "triggerType": "NodeChange", "triggerNodeType": "User", "triggerKind": "Created",
    "matchField": "email", "matchValue": "newcomer@example.com",
    "continuationType": "AddToGroup", "targetPath": "YouTube/Team",
    "createdBy": "amaier"
  }
}'
```

- **`slug(x)`** = lowercase, every non-alphanumeric → `_` (so `newcomer@example.com` → `newcomer_example_com`,
  `YouTube/Team` → `youtube_team`). The invitation node id is `slug(email)`; a re-invite upserts the same
  nodes instead of duplicating.
- Enums serialise by **name**: `"triggerType": "NodeChange"`, `"triggerKind": "Created"`,
  `"continuationType": "AddToGroup"`.
- **The invitation is what unlocks onboarding** in invitation-only mode AND what sends the email —
  create it even if you only care about the durable grant.

**Code equivalent (one call does both branches):** `hub.InviteToGroup(groupPath, email, invitedBy)` —
existing user → membership now; unknown → invitation + `AddToGroup` subscription. Returns
`GroupInviteOutcome.Added` / `.Invited`. It is the group twin of `SpaceInviteService.Invite`.

# 🚨 Email caveat — verify it actually sends

The invitation email only goes out when the portal has **`Email:Enabled = true`** (Microsoft Graph
`Mail.Send`) **AND** the `MeshWeaver.Mail.MicrosoftGraph` module landed. `Email:Enabled` defaults to
**false** → `NoOpEmailSender` just logs. If email is off, the invitation still authorises onboarding
and the durable grant still fires — but **no email is sent**; tell the user rather than reporting a
phantom send. Check the deployment's `Email__Enabled`.

`Enabled=true` with the module ABSENT is a **refused** configuration, not a working one: the
invitation emailer does not start and logs an error naming the missing module, so the invitation
stays un-emailed (`EmailSentAt` null) and goes out by itself once the module lands. It used to be
stamped as sent while nothing was delivered (#2023) — if you see that on an old record, the
invitation was never actually emailed.

# Verify — never declare done without this

1. `mcp get @{Space}/{Group}` → the group exists.
2. `mcp get @{Space}/_Access/{Group}_Access` → the grant exists with `mainNode == {Space}` and the group
   path as `accessObject`.
3. Members: `search namespace:{Space}/{Group} nodeType:GroupMembership` lists the memberships.
4. Invitees: `get @Admin/Invitation/{slug}` (Pending) AND
   `get @Admin/EventSubscription/addgroup_{slug(email)}_{slug(group)}` (Pending → Fired after they onboard).
5. Effect: an existing member reading a node under `{Space}` no longer sees `Access denied` (propagation ~1 s).

# Pitfalls — each makes membership silently grant nothing

| Symptom | Cause |
|---|---|
| Member added, still `Access denied` | group's `AccessAssignment` `mainNode` empty, or `accessObject` ≠ the group PATH |
| Member added, still no access | membership's `groups[].group` ≠ the grant's `accessObject` (must be the same path string) |
| Group in another partition doesn't work | group/memberships/grant not co-located in the space's schema — put them all under `{Space}` |
| Invitee never lands in the group | `EventSubscription` not in `Admin/EventSubscription`, or `matchField`/`matchValue` ≠ the User's email |
| No invite email | `Email:Enabled=false` (NoOp) — the grant still works, but nothing was emailed |

# Related

- [/access](/access) — the AccessAssignment shape, roles, and the `mainNode` rule this builds on
- [/space](/space) — create the space the group serves
- [Granting Access via AccessAssignments](/Doc/Architecture/GrantingAccess) · [Access Control Architecture](/Doc/Architecture/AccessControl)
