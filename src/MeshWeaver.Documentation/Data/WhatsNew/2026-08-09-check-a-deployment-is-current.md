---
Name: Checking that a deployment is up to date, from inside or outside
Category: Feature
Order: -20260809
Description: Every user can now see on About whether their build is current, and a new anonymous /api/version endpoint answers the same question from outside the portal.
Icon: ArrowSync
---

# Checking that a deployment is up to date, from inside or outside

Knowing *what* a portal is running has never been the hard part — Settings → About has
long named the version, the git commit it was built from (linked to the commit on
GitHub), the runtime and the installed plugins. The awkward question was the next one:
**is that the newest build?** Until now it could only be answered by a platform admin,
on the Updates tab, after signing in.

Both halves of that are now closed.

## On the About page, for every user

About carries one more line:

- ✅ **Up to date** — no newer build has been detected.
- ⬆️ **Update available** — followed by the version that supersedes the running one.

Only the verdict is shown. The update *strategy* (continuous, stable or off), the
CI-verified-builds setting and the update poller's own bookkeeping stay on the admin
tab, because those are decisions about the deployment rather than facts about it.

If the line is absent, that is deliberate: an installation with no update checking
configured — or one where automatic checks have been switched off — has no evidence
either way, and a reassuring "up to date" on the strength of a check that never ran
would be worse than silence.

## From outside, with no sign-in

A deploy check, an uptime monitor, or anyone comparing a running portal against the
repository needs the answer *without* a session. `GET /api/version` now returns it:

```json
{ "version": "3.0.0-rc1.ci.2340", "commit": "3e184262a3b68879bf05155e428304776154fa40" }
```

It sits beside `/health` and `/alive` and behaves like them: no authentication, and no
dependency on storage or the mesh — the two values are compiled into the build, so the
endpoint still answers when the database is having a bad day.

The response is those two fields and nothing else. No environment name, no cluster or
namespace, no configuration, no partition names, no user data. Nothing is disclosed that
was not already public: MeshWeaver is a public repository, so every version and commit
SHA is readable on GitHub already — the endpoint only says which of them is running.
