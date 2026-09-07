---
Name: A form whose record was deleted no longer breaks the page
Category: Fix
Description: Options, choices and fields tied to a record that had been deleted — or that is only written once you answer — used to fail on every redraw instead of simply showing up empty. They now draw blank, wait, and fill themselves in the moment the record appears.
Icon: Bug
Order: -20260907
---

Forms in the portal are wired straight to the record they edit: pick a delivery option in **Share ⇒
as email**, answer a quiz question, change a setting, and the choice is written to that record with
no Save button in between. That is what keeps a half-filled form alive across a sign-in round trip
or a reload.

It also means a form has something to be wired *to* — and sometimes there is nothing there:

- **The record was deleted while you had the page open.** You share a document by email, then delete
  the document (and with it its draft) in another tab. The share form is still on screen, still
  pointed at a draft that no longer exists.
- **The record is not written until you act.** A course quiz stores your answers on a record of your
  own, created by your *first* answer — so that simply opening a quiz does not leave anything behind
  for people who only looked.

Both are ordinary. Neither was handled: the form would try to open the missing record on **every
single redraw**, fail, and try again on the next one. On screen the affected choice never loaded, so
the form looked broken; behind it the portal was logging the same failure several times per redraw
— on one deployment, 473 times across four days.

**A record that is not there is now a state, not a failure.** A form asks whether the record exists
before it opens it, so the missing one is never reached for in the first place:

- **The control draws empty** and the rest of the form is fully usable.
- **It keeps watching.** The moment the record appears — you answer the first quiz question, someone
  restores what was deleted — the form fills itself in, with no reload and no reopening the page.
- **And it no longer gets in its own way.** Repeatedly reaching for a missing record made the portal
  briefly block writes to that same address, so the very failure the form was showing could hold up
  the answer you were trying to save.

Nothing changes for a form whose record is there, which is nearly all of them.

The engineering rule behind it — read *whether it is there* one way, *what it says* another, and
never paper over the difference — is in
[CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) and
[Data Binding](/Doc/GUI/DataBinding).
