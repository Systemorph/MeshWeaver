---
Name: The notices inside a code cell get a language
Category: Fix
Description: When a page cannot run its code, or is still starting up to run it, the cell says so — and it said so in English to everyone, in the middle of a page that was otherwise in the reader's language. Those two sentences are now translated text like every other word the portal owns, and a test keeps them that way.
Icon: Globe
Order: -20260904
---

# The notices inside a code cell get a language

A lesson with a runnable code block does not always have somewhere to run it. When it does not, the
cell replaces its output area with a short sentence saying so; while the sandbox is still starting
up, it shows a different one. Both are the portal talking about itself, and both were typed straight
into the view in English — so a reader whose portal is in German met two English sentences sitting
inside the code cell of a page that was otherwise in German.

Under the rule settled earlier the same day, that is not a grey area: the portal's own words follow the
reader, an author's words stay as the author wrote them, and a sentence belonging to neither is a
bug rather than a third category. These two belonged to nobody. They do now — they are ordinary
translated text, and they say the same thing in German that they say in English.

**What is left is the wiring, and it lands with the next module release.** The words and their
translations are in place; the two views that draw the cell have to start telling them who is
reading, which happens in the module that owns those views rather than in the platform. Until then
the sentences still read in English — the same staged shape the code cell's Run button went through
this morning.

A test now renders both notices in both languages and fails if either one turns back into a fixed
English sentence. It was written by breaking it three ways first, including the quiet one: a
mistyped name for a translation looks fine to any check that compares the two languages against each
other, because both then fall back to the same wrong text. Two languages agreeing exactly is what
the test treats as the alarm.
