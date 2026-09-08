---
Name: Operate a deployment from the portal, not the cluster
Category: Feature
Description: Rolling, restarting, suspending, auditing or reconciling an instance is an action you file on the control instance — the operator in the cluster carries it out, and you never need cluster credentials. The documentation now says so on every page that used to hand you a kubectl command, and says honestly which reads still have no portal answer yet.
Icon: Cloud
Order: -20260908
---

Every instance in the fleet is a **record** on the control instance, and every change to it is an
**action** — a `Hosting/InstanceAction` you create with the instance's name, what you want
(`Roll`, `Restart`, `Suspend`, `Reactivate`, `Audit`, `Reconcile`, …) and a confirmation. The
operator running inside the cluster does the rest and writes each phase back onto the same node,
so you watch one page instead of a terminal.

## What changed

Nothing in the cluster. What changed is the **rule**, and the documentation that states it: an
`az aks command invoke …` / `kubectl …` line is no longer the procedure. Every page that used to
open with one now names the action that does the same thing, and keeps the old command only as
break-glass — for the case where the control plane itself cannot act — or as the measurement
behind a war story.

## What is honest about it

Three questions still have no portal answer today: which image and how many restarts each
replica has, what the process logged at a given minute, and whether a particular replica can
load a particular node type. The new page
[Operating from the portal, not the cluster](/Doc/Architecture/OperatingFromThePortal) says so,
names the read-only break-glass shape for each, and is the page the work closing those gaps
points back at.
