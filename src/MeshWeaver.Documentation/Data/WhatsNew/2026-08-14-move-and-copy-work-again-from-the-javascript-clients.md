---
Name: Move, copy and Run work again from the JavaScript SDKs
Category: Fix
Description: Node move/copy and the Run trigger sent field names the server never matched, so the operations quietly did nothing.
Icon: Sparkle
Order: -20260814
---

# Move, copy and Run work again from the JavaScript SDKs

Moving or copying a node from the browser or Node.js client sent the request under the wrong field
names, and the server has no way to report that: it simply builds the request with empty paths and
the operation completes having done nothing. The same silence hid a broken Run trigger, a partial
update request that never identified the node it was meant to change, and a node subscription that
could not be resolved.

The clients now send exactly the names the server declares, and a new check compares every field
name against the server's own definitions on each build — so a future rename turns the build red
instead of turning a feature into a no-op.
