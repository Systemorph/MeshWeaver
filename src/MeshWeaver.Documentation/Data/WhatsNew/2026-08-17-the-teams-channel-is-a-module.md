---
Name: The Teams channel is a module
Category: Feature
Description: The Microsoft Teams bot channel moved into its own MeshWeaver.Teams module, so only deployments that actually run a Teams bot carry it.
Icon: Chat
Order: -20260817
---

# The Teams channel is a module

The Microsoft Teams bot channel — the messaging endpoint, the router that turns an inbound Teams
message into an agent thread round, and the sender that delivers replies back — now ships as
`MeshWeaver.Teams` instead of being compiled into every portal.

Teams is a channel a deployment either has or does not: it needs an Azure Bot resource and a
published Teams app, which most deployments never provision. Listing the DLL is now that decision,
rather than every portal carrying the channel and keeping it switched off.

Nothing changes for a portal that runs the bot. It keeps the same `Teams` configuration, the same
messaging route, and the same behaviour when the bot is unconfigured: the endpoint answers 404 and
the reply sender stays inert, so an un-provisioned deployment never responds to the Bot Framework.

The conversation links stay where they were, in the platform, so existing Teams conversations keep
reading correctly whether or not the module is listed.
