---
Name: A control no longer stops updating when one value cannot be converted
Category: Fix
Description: When a value bound to a control could not be converted to the type the control expected, the error did not just skip that one update — it ended the binding, and the control silently stopped updating for as long as the page stayed open. Bindings now survive an unconvertible value.
Icon: Link
Order: -20260909
---

# A control no longer stops updating when one value cannot be converted

A label, or any bound control, could stop updating entirely and show nothing — with nothing on the
page to say why.

The cause was the conversion that turns a bound value into the type a control expects. When it met a
value it had no reading for, it raised an error. That error travelled out of the live binding rather
than being confined to the single update that caused it, and ending a binding ends it for good: the
control kept whatever it was showing and never changed again while the page was open.

A value the conversion cannot read now leaves the control at its default for that update, and the
binding keeps running. Structured values — a list or an object bound where text was expected — now
render as their text instead of stopping the control.
