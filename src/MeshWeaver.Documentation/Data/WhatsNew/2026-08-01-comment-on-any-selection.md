---
Name: Select text and comment — on any content, not just documents
Category: What's New
Description: Highlighting a passage and commenting on it used to work only on markdown documents. The affordance is now a wrapper any view can put around any content, so posts and other rendered text can offer it too.
Icon: Sparkle
---

# Select text and comment — on any content

Selecting a passage and leaving a comment pinned to exactly that text was a documents-only
feature. Everywhere else — a social post, a rendered block, a composed page — you could only
comment on the item as a whole, which makes it hard to say *which sentence* you mean.

The affordance is now something any view can wrap around any content. The comment is anchored to
the words you selected, and because the anchor is stored as a range in the underlying text rather
than as a marker inside it, the highlight follows the passage when the text around it is later
edited — and readers who are allowed to comment but not edit can still leave one.

Nothing changes for documents: they behave exactly as before, and both surfaces now share the
same implementation, so a comment looks and works the same wherever you leave it.
