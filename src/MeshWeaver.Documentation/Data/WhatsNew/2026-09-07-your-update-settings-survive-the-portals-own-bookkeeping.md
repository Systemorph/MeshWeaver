---
Name: Your update settings survive the portal's own bookkeeping
Category: Fix
Description: The portal writes to its update record every time it checks for a new version. If it could not read what was already there, it used to write a blank record over it — losing the auto-update setting, the version it had found and every compatibility verdict. It now leaves the record alone and says so.
Icon: ShieldCheckmark
Order: -20260907
---

# Your update settings survive the portal's own bookkeeping

Every time the portal checks for a new version it notes the result on the same record that holds
your auto-update setting: when it last looked, what it found, whether an update is being held back,
and which candidate versions were verified against your installed modules.

To add a note it first has to read what is already there. If that read failed — a field written in a
shape this build does not recognise is enough — the portal treated it as *"there is nothing here"*
and wrote a fresh, blank record. One routine check was enough to erase the auto-update setting, the
newest version it had found, an active hold and every recorded compatibility verdict, silently and
with no way to tell afterwards that anything had been lost.

Reading and writing are now different questions. A record the portal cannot read is **left exactly
as it is**: the bookkeeping note is refused, the reason is written to the log naming the record and
what could not be read, and the update check itself carries on unaffected. Nothing the portal could
not read gets replaced by something it made up.
