---
Name: An installation pulls its platform images from the mirror
Category: Feature
Description: Every installation except the one serving the container-image mirror can now pull its portal and migration images from that mirror instead of the upstream registry — when it is first created and every time it updates itself — using the plugin-registry key it already holds. The chart declares it in three values, the self-updater lists releases through the mirror, the operator creates the pull credential, and the operator image is published on every merge.
Icon: Cube
Order: -20260908
---

# An installation pulls its platform images from the mirror

The platform's container images — the portal and its database migration — used to be pulled from
the upstream Azure Container Registry by every installation, which meant every installation held
an upstream registry credential beside the plugin-registry key it already had. The read-through
mirror one installation serves at `/v2` removes that second credential: a caller authenticates
with its instance key, and the mirror holds the one upstream credential for everyone.

This release makes the mirror the source of platform images for **every installation except the
one serving it** — that instance cannot serve the image that boots it, so it stays on the
upstream permanently ([A Container Registry in Memex](/Doc/Architecture/ContainerRegistryInMemex)).

**In the chart**, three values describe a consumer: `selfUpdate.registry` names the mirror host,
`portal.imagePullSecret` names the pull credential its pods use for it — rendered on the portal
Deployment *and* the migration Job, so a roll can never leave the Job unable to pull — and the
mirror instance's own configuration lives under `containerImages`, rendered key by key and off
unless set. A chart can now also **create an installation's persistent volumes** (`persistence.<name>.create: true`,
with size and storage class required and the claims kept on uninstall), so a newly provisioned
installation starts with real storage rather than an empty directory that vanishes on restart.

**In the self-updater**, a registry that is not an Azure Container Registry is listed through the
OCI Distribution API with the installation's plugin-registry key — the same handshake any registry
client speaks, every page of a paginated listing followed, and a refused credential reported as an
error rather than mistaken for "nothing newer". An installation on the upstream registry behaves
exactly as before.

**In the operator**, `hosting-pull-secret` turns the instance key in Key Vault into the pull
credential its namespace needs — creating the namespace first, since it runs before the release
is deployed — and never prints the key. The operator image itself is now published on every
merge, so a fix to a lifecycle script reaches the Jobs that run it instead of only the repository.
