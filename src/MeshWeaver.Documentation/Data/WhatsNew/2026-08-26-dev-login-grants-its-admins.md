---
Name: The developer login can grant its administrators again
Category: Fix
Description: A self-hosted or local portal could enable the built-in developer login, but the list of ids it should grant platform admin to never reached the container — so you could sign in and then administer nothing. The setting is now rendered like its partner.
Icon: PersonKey
Order: -20260826
---

# The developer login can grant its administrators again

The built-in developer login is two settings, not one: `Authentication:EnableDevLogin` turns it on,
and `Authentication:DevAdminUsers` names the ids that should become platform administrators when
they sign in that way. Only the first was rendered into the portal's configuration. The second was
accepted in a deployment's values file, reported as applied, and then reached nothing.

The result was a portal you could log into and then administer nothing on — with no error anywhere,
because a setting the configuration template does not list is simply absent rather than rejected.
That reads as "the developer login does not work", which is the wrong place to look.

Both halves are now rendered together, and a test asserts the pair rather than either alone, so one
cannot be added again without the other.

This is the third setting to go missing the same way, after the module root and the required-module
list. The general guard added with those catches any key a *tracked* values file sets; these two are
set by a developer in their own local overlay, where nothing was watching.
