---
Name: The Executive Assistant can edit a meeting instead of replacing it
Category: Fix
Description: Booking without attendees works again, and amending an event now reads it first and patches it in place rather than cancelling and recreating.
Icon: Calendar
Order: -20260825
---

# The Executive Assistant can edit a meeting instead of replacing it

Asking the Executive Assistant to book a meeting **without** inviting anyone used to fail with an
unhelpful "were unable to deserialize" — and because the message named nothing, the assistant would
retry with guesses instead of fixing it. That request now succeeds, and when Microsoft does refuse
something the assistant is told which field it objected to.

Amending an event is also no longer destructive. The assistant could previously only cancel a meeting
and create a new one, with no way to read the existing agenda first, so "add one line to the notes"
could quietly wipe everything already written there. It can now read an event in full and change just
the parts you asked about, leaving the rest alone.

One timing bug went with them: a meeting requested with an explicit time-zone offset (*"14:00 my
time"*) was booked at that number of hours **UTC**, landing the invitation at the wrong hour.
