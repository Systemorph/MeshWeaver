---
Name: Mail over Microsoft Graph is a module
Category: Feature
Description: System email, inbound mail intake and the Executive Assistant's mailbox tools moved into their own module, taking the 43 MB Microsoft Graph SDK out of every deployment that does not send mail.
Icon: Mail
Order: -20260817
---

# Mail over Microsoft Graph is a module

Everything that talks to Microsoft Graph — sending system email, taking delivery of inbound mail,
and the Executive Assistant's mailbox tools — now ships as `MeshWeaver.Mail.MicrosoftGraph`
instead of being compiled into every portal.

The Microsoft Graph SDK was the heaviest dependency in the image: **43 MB across ten assemblies**,
carried by every deployment for the benefit of the ones that send mail. It also cost at runtime,
where `Microsoft.Graph.dll` alone materializes a 41 MiB block of native metadata in the script
engine's reference set — previously identified as a direct contributor to memory pressure.

Nothing changes for a portal that sends mail: it lists the module, keeps the same `Email`
configuration, and the webhook, senders and EA tools behave exactly as before. A portal that does
not send mail now carries none of it, and its invitation and outbound-mail services keep working
against the built-in no-op sender rather than failing to start.

Everything mail-shaped that never touched the SDK stayed where it was — the invitation emailer,
the outbound drain, and the per-user consent flow that lets the assistant act on your own mailbox.
