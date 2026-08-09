---
title: "\"Access denied\" and \"Not found\" now mean what they say"
date: 2026-08-09
---

# "Access denied" and "Not found" now mean what they say

A wrong error message is annoying. A wrong error message that sounds *specific* is expensive,
because you act on it. Three places in the portal used to answer "we could not find out" with a
confident, detailed negative — and each one sent someone off to fix a problem that did not exist.

**"Access denied: you lack Read permission on X."** When the permission lookup itself broke — a
wedged cache, a database blackout — the portal reported it as a denial, naming you, the
permission and the path. Everything about that sentence was false except your name. People took
it at face value and asked an administrator for access they already had, and the administrator
found nothing wrong, because nothing was wrong with their access.

**"Not found: X."** A read that timed out looked exactly like a node that did not exist. For an
AI agent this is worse than for a person: told a node is missing, an agent helpfully re-creates
it — duplicating content — or deletes and rebuilds the "broken" path. Against a node that was
merely unreachable for a moment, that is destructive, and we invited it.

**"User unknown."** The index of users is filled in the background after a restart. Until it
arrives it is empty — and an empty index answered "no such user" instead of "ask me again in a
moment". "No such user" is the input that drives sign-up and provisioning, which is how a
storage stall once redirected a signed-in user to the sign-up form.

All three now distinguish *a verdict* from *no verdict*, decided in the one place that knows —
the check or read that gave up. When the portal cannot answer, it says so: the content is still
there, your access has not changed, and the fix is to wait a moment and reload rather than to
delete anything or to go asking for permissions.

Refusals are unchanged in both directions. A real denial is still a denial, a genuinely missing
node still reports Not found, and a genuinely new user still goes to onboarding — a check that
cannot run still refuses, it simply no longer claims to know why.
