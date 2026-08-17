---
Name: Recycle no longer reads as "delete" in German
Category: Fix
Description: The German menu entry for Recycle said "In den Papierkorb" — move to the recycle bin — describing deletion, which is the opposite of what the action does.
Icon: Translate
Order: -20260817
---

# Recycle no longer reads as "delete" in German

In German the node menu's **Recycle** entry read **"In den Papierkorb"** — *move to the recycle
bin*. That describes deleting the node. The action does the opposite of deleting anything: it
restarts the node's hub so it picks up the latest compiled build, and leaves the node and all its
content untouched.

A destructive-sounding label on a non-destructive action is worse than an unclear one. It has two
failure modes and both are bad: people avoid a feature they need, or they click it *expecting* to
delete something and are surprised when the node is still there.

The entry now reads **"Hub neu starten"**, and the confirmation page that Recycle now shows says in
both languages that nothing is deleted and no content changes.

If you have been avoiding Recycle in the German UI because it looked like a delete button — it never
was one.
