---
Name: Content that does not fit its type is refused instead of stored
Category: Fix
Description: A write that put the wrong kind of value into a field — or whose fields matched nothing the type declares — used to be saved exactly as sent and then read back as an empty node. It is now refused, in your language, naming the field and what it should have held.
Icon: ShieldCheckmark
Order: -20260917
---

# Content that does not fit its type is refused instead of stored

Every node type declares the shape of its content: which fields exist and what each one holds. Until
now nothing checked a write against that declaration. A write that put a whole object into a field
declared as text — or whose field names matched nothing the type knows — was accepted, given a new
version, and stored exactly as sent.

Nothing failed at that moment. The damage showed up later, and somewhere else: the record could no
longer be read back as its own type, so every view of it came up empty. A page would show *"No
content yet"* over a document that was still there in full, and the person who could have fixed the
write in one retry was long gone.

Such a write is now refused at the point it is made, with a message in your language naming the
field, what that field was declared to hold, and what was sent instead — so it can be corrected
immediately.

The check is deliberately narrow, so that writes which were never the problem keep working. Content
carrying an extra field alongside real ones still lands, as it always did: that is what an older or
newer version of the same record looks like, and dropping such a field is already reported. Only two
things are refused — a field whose value contradicts its declaration, and content in which not one
field matches the type at all.
