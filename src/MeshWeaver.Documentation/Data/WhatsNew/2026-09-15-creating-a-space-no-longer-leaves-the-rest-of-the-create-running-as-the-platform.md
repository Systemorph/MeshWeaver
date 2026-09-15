---
Name: Creating a Space no longer leaves the rest of the create running as the platform
Category: Fix
Description: The grant that makes you the owner of a new Space ran as the platform and forgot to hand your identity back — so everything the create did next ran with full rights on some runs and with yours on others, which is why creating a page in a brand-new Space sometimes answered "Create permission required".
Icon: ShieldKeyhole
Order: -20260915
---

# Creating a Space no longer leaves the rest of the create running as the platform

Creating a Space does several things at once: it provisions the Space's own storage, it writes the
grant that makes **you** its owner, and it registers the Space with the rest of the mesh so every
part of the platform agrees the Space exists.

The grant is written by the platform rather than by you, and it has to be: the Space is brand new,
so at the moment the grant is written there is nothing you hold on it — including the right to write
that grant. That part was correct.

What was wrong is that the platform identity was never handed back. The step that borrowed it
finished on a different thread from the one it started on, so the borrowing was opened in one place
and closed nowhere — and **the rest of the create carried on with the platform's full rights**. Not
always: whether the next step ran as the platform or as you depended on timing that nothing in the
code controlled. Both identities were measured in a single run, 41 milliseconds apart.

That is why it surfaced as something intermittent. The step that registers the Space with the mesh
is a write to a platform-owned record, which you hold nothing on; on the runs where it inherited the
borrowed identity it succeeded, and on the runs where it did not it was refused — and a Space that
was never fully registered is one where creating your first page can come back **"Access denied:
Create permission required"**, on a Space you had just created and do own.

The borrowing is now opened and closed in one place, so the grant is still written by the platform
and the create continues as you. Nothing about who may create a Space, or what you get when you do,
has changed — a create that works today works exactly the same way, on every run rather than on
most of them.

A write that quietly runs with more rights than the person who asked for it is the more serious half
of this, even when it happens to succeed, so the boundary is now checked by a test rather than
inferred from a log.
