---
Name: A create that is refused now explains itself in your language
Category: Fix
Description: The reasons a new node could be refused — an unregistered type, a name already taken, a batch with a duplicate path, a store that could not be reached — were written in English, whatever language you read the portal in. Every sentence the portal itself writes is now translated, and the Create dialog shows you the translated one; text that came from somewhere else, such as a database driver's own words, is still shown exactly as it arrived.
Icon: Translate
Order: -20260916
---

# A create that is refused now explains itself in your language

The portal ships in English and German, and almost everything you read follows the language you
chose. Creating a node was one of the places where that stopped being true the moment something went
wrong: the confirmation was translated, the **refusal** never was.

So a German reader who tried to create a page with a type that did not exist got

> NodeType 'Foo/Bar' is not registered

and a German reader whose batch of pages contained the same path twice got

> Duplicate path in batch: 'Space/Page'

— in the middle of an otherwise German screen, with no clue that the sentence was a real,
actionable message rather than an internal error that had leaked out.

## What changed

Every sentence the create path **writes itself** about a refusal is now translated — the whole
surface at once, not one message at a time. (Text that reaches you from somewhere else is a separate
case, and the last section says which cases those are and why they stay as they arrived.) That
includes:

- a type that is not registered, and a path that is already taken;
- a name or an identifier left empty, and a page with neither a type nor any content;
- a batch that contains the same path twice, or an entry that cannot travel in a batch at all;
- a store that could not be reached, with the two different things that can mean (nothing was
  written, or part of the batch may have landed);
- a create that was cancelled, one whose follow-up step failed and was rolled back, and one whose
  page moved away mid-flight.

The **Create dialog** now shows you that translated sentence, rather than the English one it used to
repeat. The dialog's own "you do not have permission to create here" message was itself hard-coded
English; it is translated too.

Converting the whole surface in one go is deliberate. Translating half of it would have been worse
than translating none: two refusals from the same screen, one German and one English, reads like a
broken translation rather than like a feature that has not arrived yet.

## What deliberately did not change

Two kinds of text stay in English on purpose.

**Words the portal did not write.** When a refusal is a database driver's own message, a validation
rule contributed by an installed package, or the report of a rollback assembled from counts that
change every time, it is shown exactly as it arrived. There is nothing honest to translate, and
inventing a German frame around an English fragment would only make it harder to read.

**The value other software reads.** Underneath the message you see, each refusal also travels as a
fixed English string that the platform's own services record in logs and match on. That one is
unchanged, so operators keep searching their logs in a single language — and it is precisely because
it is unchanged that the sentence *you* read could become yours.
