---
Name: A page you may not open is no longer reported as a broken page
Category: Fix
Description: Access control turning someone away was recorded as a system failure — at the severity that files an incident, and without ever naming the page. Both are fixed; the refusal itself was always correct.
Icon: ShieldKeyhole
Order: -20260811
---

# A page you may not open is no longer reported as a broken page

Opening a page you do not have access to is an ordinary, expected outcome: the portal says so, you
move on. Behind the scenes it was being written down as something else entirely — a system failure,
at the severity reserved for defects, which is the severity that automatically opens an engineering
ticket. Over one recent day, one person opening two pages they were not entitled to see produced
four such records and a ticket about a portal that was working exactly as designed.

The record was not just alarming, it was unusable. It ended with the words "for area (null)" —
naming nothing. Every page is assembled from named areas, and the name is the one detail that says
*which* part of the page was refused. It went missing in precisely the case where it mattered most:
whenever the portal is asked for a page's main content, it is asked without a name, because "the
main one" is what is meant. The portal resolves that to a real area immediately and renders it —
but the record was written from the unresolved request, so it reported nothing.

Now the record is written from the area actually being rendered, so it always carries a name, and a
refusal is recorded as a refusal rather than as a fault. The portal's own display already made this
distinction; only the server-side record disagreed. Nothing about the decision changed — someone
without access is still turned away, still told so on screen, and the full detail is still written
down. A genuine defect in a page, and a check that could reach no verdict at all, both still report
at full severity and still open a ticket.
