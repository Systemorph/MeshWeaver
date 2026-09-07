---
nodeType: WhatsNew
title: A bundle publication reaches the repositories that depend on it — and never its own publisher
category: Fix
date: 2026-09-07
---

When a node repository seals a bundle publication, its build lane registers the record with the
control instance, which wakes the repositories that must rebake against it. On 2026-09-07 that wake
went to the whole fleet, the publisher included: Crm published, memex woke Crm and Reinsurance, both
rebaked the same inputs and registered the same digest, and memex woke both again — a run on each
repository every few seconds, the GitHub App's API limit exhausted, and the organisation's Actions
queue backed up behind the storm.

The publication record now carries the repository's **declared upstreams** (the lane's
`upstream-sources`, the one place a repository says what it depends on), and the control instance
routes a publication to exactly the registered repositories whose upstreams name its source — never
the publisher, never a repository that does not depend on it, and never twice for the same sealed
identity and digest. On the receiving side the lane refuses, red, a wake that names the repository's
own source, instead of baking the same bytes again.

A repository joins the directed wave with its next publication after moving to a lane that sends the
declaration; until then it rebakes on its schedule poll, and the control instance's log says so. The
platform-release wave is unchanged: a new framework concerns every registry source.
