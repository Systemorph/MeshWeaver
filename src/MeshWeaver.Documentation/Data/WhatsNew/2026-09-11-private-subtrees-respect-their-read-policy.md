---
Name: Private subtrees respect their read policy
Category: Fix
Description: A read restriction on a child now applies when opening it directly, even when its parent is public.
Icon: Shield
Order: -20260911
---

# Private subtrees respect their read policy

A public page can contain a private subtree. Its read restriction now applies when someone opens
a child directly, matching the existing policy precedence used by PostgreSQL listings.

Ordinary public content stays readable. A public grant placed at the same scope as a read cap
retains its existing precedence; a deeper read cap restricts inherited public access.
