---
Name: Anyone signed in can create a new CRM client — and any other partition a package declares
Category: Fix
Description: Creating a top-level CRM client was refused for everyone, platform admins included. A package type that declares it owns its partition is now created like a Space — by any signed-in user, who becomes its admin.
Icon: Building
Order: -20260915
---

# Anyone signed in can create a new CRM client

The CRM keeps each client in its own partition: a new client is a **top-level** node of type
`Crm/Client`, and the CRM guide says to create exactly that. Until now the platform refused it for
everyone — platform admins included — with *"Access denied: Create permission required"*. The only
way in was to create a Space and retype it.

A Space has always been creatable by any signed-in user, who becomes its admin. That rule now
applies to **every** type whose definition declares that it owns its partition, including types a
package declares rather than the platform itself — a CRM client is the first of them. Create one,
and you get what a Space's creator gets: the partition is set up, routed, and yours to administer.

Nothing else loosens: an ordinary page still cannot be placed at the top level, and a visitor who
is not signed in cannot create a partition at all.
