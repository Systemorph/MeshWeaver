---
Name: Plugin bundles are pulled from the fleet registry
Category: Feature
Description: A self-hosted portal can now materialise its sealed plugin bundles from cr.meshweaver.cloud before it starts, with the same credential that pulls its image — and the registry lets each installation pull exactly the bundles its licence covers, nothing more.
Icon: CloudArrowDown
Order: -20260908
---

# Plugin bundles are pulled from the fleet registry

Sealed plugin publications are OCI artifacts in the fleet registry, beside the portal images. A
portal that opts in (`bundles.registry` and `bundles.sources` in its Helm values) runs a
`bundle-fetch` init container before it starts: it pulls the publication for the portal's own
framework identity and lays it out exactly where the pre-warm already looks, so the portal keeps
reading a filesystem and needs no registry client, no network and no mesh to seed. A publication
that is not sealed for that identity is logged and skipped; a fetch that cannot complete holds the
pod rather than starting it on a half-fetched shelf.

The registry decides per repository what an installation may pull: the auth server exchanges the
presented instance key at memex and turns the licence it answers into the token's labels, so a
plan-scoped licence reaches its source's bundle repositories and never the publication whole,
while releases and images stay readable by every authenticated installation. Publishing is one
standalone script, `push-bundle-publication.sh`, that refuses an unsealed publication before the
first push and moves the identity tag last — the tag move is the seal.

Design: [Plugin Bundles in the Registry](/Doc/Architecture/PluginBundlesInTheRegistry).
