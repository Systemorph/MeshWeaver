---
Name: Articles keep their authors, tags and abstract
Category: Fix
Description: Creating an article could quietly discard the metadata you supplied — authors, tags, abstract, thumbnail — leaving a page that looked like it had simply been written without them. The values now survive.
Icon: Sparkle
Order: -20260813
---

# Articles keep their authors, tags and abstract

Creating an article and supplying its metadata — who wrote it, how it is tagged, its abstract, its
thumbnail — could end with all of that missing. Nothing reported a problem: the article was created,
it opened, and the absent metadata looked exactly as if the author had never filled it in.

The cause was a mismatch in how the Article type was described. A content type declares the shape of
what it holds, and the platform trusts that declaration when it needs to interpret stored content.
Article's declaration named a shape that nothing actually used — one that had no place to put an
abstract at all. So on the way back out, the content was reinterpreted into that shape: the abstract
had nowhere to land and was dropped, and the rest no longer matched what the article's own views
were looking for.

It only bit content created directly — through an assistant, an integration, or the API — because
articles written as files carry an explicit note of their shape, and that note took precedence. That
is why existing articles look right and the problem stayed hidden.

The declaration now matches what articles really contain, so the four fields survive being written
and read back.

Alongside the fix, the platform no longer stays quiet when this happens. Whenever stored content is
interpreted into a shape that cannot hold part of it, that is now reported — naming the fields that
could not be carried and why — instead of returning a plausible-looking result with values silently
missing. Content whose type is simply an older version of the right one is unaffected: those extra
fields were already preserved and still are.
