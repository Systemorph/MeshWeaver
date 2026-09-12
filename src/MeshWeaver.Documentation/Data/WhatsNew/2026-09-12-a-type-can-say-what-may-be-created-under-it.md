---
Name: A type can say what may be created under it
Category: Fix
Description: A NodeType's creatableTypes setting is finally read. Declaring it now both adds a type to the Create menu under instances of that type and limits the menu to what was declared — until now it was documented, accepted and ignored.
Icon: Add
Order: -20260912
---

# A type can say what may be created under it

A node type can declare what may be created under its instances:

```json
{ "creatableTypes": ["Crm/Question"] }
```

That setting has been documented and accepted for a long time, and until now nothing read it. The
Create form worked out its own list instead, from where the node sits: a type was offered if it
lived at the top level or somewhere on the path above the node you were creating under. Nothing a
type declared could add to that list, and nothing could take anything off it.

The effect was a feature that appeared to work and did nothing. A CRM module shipped exactly this
declaration — offers may contain questions — and on the portal an offer's Create menu listed around
ninety types, not one of them from the CRM module. There was no error and no empty state to notice:
the entry simply was not there. Anything else relying on the setting failed the same silent way.

The Create form now asks the platform the question rather than answering it itself, so the setting
does what it says:

- **It adds.** A declared type is offered even when it lives in a different part of the mesh
  entirely — which is the case the old list could never reach, and the reason the CRM module's
  declaration did nothing.
- **It limits.** Where a type declares a list, that list is what instances of it offer. A type
  discovered nearby but not declared is withheld.
- **It leaves the common case alone.** A type that declares nothing restricts nothing: the menu
  under it still contains everything it contained before. That is pinned by a test, because a menu
  that quietly got shorter would have been the same invisible failure aimed the other way.

Two smaller corrections travel with it. Types that had opted out of being created — releases,
builds, partitions — are honoured everywhere the menu is assembled. And the `includeGlobalTypes`
switch works, so a type that limits its menu can also decide whether the always-available basics
(Markdown, Thread, Agent, Node type) still ride along.
