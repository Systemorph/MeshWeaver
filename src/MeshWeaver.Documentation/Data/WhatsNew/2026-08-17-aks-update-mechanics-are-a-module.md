---
Name: The AKS update mechanics are a module
Category: Feature
Description: Reading image tags from a container registry, patching Kubernetes deployments and provisioning cluster instances moved into their own module — while the self-update poller stays in the platform, where it belongs.
Icon: CloudArrowUp
Order: -20260817
---

# The AKS update mechanics are a module

The parts of self-update that are specific to running on AKS — reading image tags from an Azure
Container Registry, patching Kubernetes Deployments, and provisioning per-instance workloads on the
cluster — now ship as `MeshWeaver.SelfUpdate.Aks` rather than being compiled into every portal.

**The self-update poller itself stays in the platform, and always will.** Self-update is how a
deployment receives new bits, modules included; putting it behind a module would mean an install
that lost the module could no longer update anything — including re-installing the module. What is
genuinely optional is the cluster mechanics, so that is what moved. The update policy, the version
selection, and the status you see on the About tab are all unchanged and still built in.

An install without the module runs **detect-and-notify**: it still watches for new versions and
still reports what it finds, but patches nothing. That is not a new state — it is exactly what a
non-Kubernetes install has always done, now reached by leaving a module off a list instead of by
happening not to run in a cluster. The startup log says so plainly, naming the module to add.

Nothing changes for the AKS-hosted portals, which list the module and roll themselves exactly as
before.
