---
Name: Instances get an identity, a licence and a setup app — the design
Category: Feature
Description: Design page: an instance is a partition on the registry, authenticates with a JWT, carries its plan on its own node, sets itself up through the Hosting app, and its environment changes only through its Deployment record
Icon: Sparkle
Order: -20260830
---

# Instances get an identity, a licence and a setup app — the design

A new architecture page, [Instance Identity and Setup](/Doc/Architecture/InstanceIdentityAndSetup), sets out how a MeshWeaver instance will be identified, licensed and set up from now on. Every instance becomes its own partition on the registry — the way a user is — with its plan on its own node, so a global admin promotes an instance by changing one field and the registry checks that plan on every package it serves. Instances authenticate with a short-lived JWT instead of a long-lived key on every request. A first-run setup app asks which database (PostgreSQL preselected), where it runs (a Postgres set up for you by default), which modules (all free ones preselected; higher tiers selectable when your plan covers them) and the instance id, then registers the instance as free. Environments change only through the instance's Deployment record. The page names what already exists, the gaps, and the four slices that deliver it, with end-to-end tests as the proof.
