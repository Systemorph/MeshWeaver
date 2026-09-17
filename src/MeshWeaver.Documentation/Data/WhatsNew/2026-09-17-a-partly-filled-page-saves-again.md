---
nodeType: WhatsNew
Name: A partly filled page saves again
Category: Fix
Description: "A save that left out a field the page requires started failing with a raw serializer message instead of being accepted. The guard that reads content on write was meant to allow exactly that shape; it was letting the error escape instead."
Icon: DocumentSave
Order: -20260917
---

A write that did not carry every field its content type requires — a markdown page saved without its
body, a record filled in one field at a time — began failing with a message no reader was meant to
see:

```text
JSON deserialization for type 'MeshWeaver.Markdown.MarkdownContent' was missing required
properties including: 'content'.
```

Nothing was wrong with those writes. The guard introduced with
[Content Schema On Write](@/Doc/Architecture/ContentSchemaOnWrite) reads proposed content against the
shape its NodeType declares, and it refuses exactly two things: a member whose value contradicts its
declared type, and content that shares no member at all with that type. A **missing** field is
neither — it is the ordinary shape of a partial save, and the guard's own rule says so.

The guard tested that shape by trying to read the content and catching the failure. It caught the
failure that names a member and the failure that means "this process has no contract for the type",
and a missing required field is neither of those, so that one error travelled out of the guard
untouched — and an error leaving a guard fails the write just as firmly as a refusal, only without
the explanation, the localization, or the name of what to fix.

A save that leaves a required field empty is accepted again. What the guard refuses is unchanged:
content whose member contradicts its declared type, and content the type shares no member with are
still refused, still naming the node, the member and what was sent — and the second of those is now
reached for types that carry a required field too, where it previously could not be.
