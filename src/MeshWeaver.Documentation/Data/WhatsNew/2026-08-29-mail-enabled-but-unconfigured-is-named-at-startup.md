---
Name: A portal that says mail is on now proves it at startup
Category: Fix
Description: Switching mail on without finishing its settings is reported by name when the portal starts, instead of quietly delivering nothing.
Icon: Sparkle
Order: -20260829
---

# A portal that says mail is on now proves it at startup

Mail could be switched on (`Email:Enabled=true`) with its settings half-filled in, and everything
looked fine: the portal started, every page served, the health check was green — and not one message
went out. Invitations, notifications and shared documents piled up unsent, with nothing on any screen
to say so. The only sign was a single line in a server log.

Now the portal checks that claim when it starts. If mail is switched on but a setting it needs is
missing, the portal refuses to start and says exactly which ones — in both the form the documentation
uses (`Email:TenantId`) and the form an operator actually sets (`Email__TenantId`) — along with every
way out: fill them in, switch to a managed identity, or turn mail off.

Turning mail **off** is still a complete, supported answer, and an installation that never wanted mail
is untouched: a blank or absent mail section starts exactly as before. Only an installation that
*claims* mail it cannot send is stopped, and it was never sending anything to begin with.
