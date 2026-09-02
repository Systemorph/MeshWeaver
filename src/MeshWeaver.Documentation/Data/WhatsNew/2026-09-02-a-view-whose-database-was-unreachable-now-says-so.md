---
Name: A view whose database was unreachable now says so
Category: Fix
Description: When the service storing your content briefly stopped answering, a page area showed "This area failed to render" plus a raw database error — as if the view itself were broken. It now says the content is temporarily unavailable and worth reloading, and the technical detail goes to the operators instead of to you.
Icon: DatabaseWarning
Order: -20260902
---

# A view whose database was unreachable now says so

Every so often the service that stores your content stops answering for a few seconds — a network
blip, a restart, a moment of overload. The platform already handles this: a read that cannot reach
the store is retried a few times, spaced out, before anyone is told anything. Most blips are over
before you could notice.

What was missing was the honest answer for the ones that are not.

When a blip outlasted those retries, the area you were looking at showed this:

> ⚠️ **This area failed to render.**
>
> ```
> Npgsql.NpgsqlException (0x80004005): The operation has timed out
> ```

Two things are wrong with that, and they compound. The message says *this area* failed — so anyone
reading it, including whoever you forwarded it to, starts looking for a bug in the view. And the
line underneath is the database driver talking to engineers, including the address of a machine you
have no business seeing. Meanwhile the log the operators watch said the same thing: *"Rendering
failed for area Catalog"* — pointing at the Catalog view, which was fine.

Nothing was broken. The content was all there. The store just did not answer in time.

**The area now says that instead:**

> **This view is temporarily unavailable.**
>
> The service that stores this content did not answer in time. Nothing is lost and nothing is
> broken — please wait a moment and reload the page.

Three deliberate choices sit behind that wording:

- **It is not a retry.** The retry already happened, before you saw anything. Spinning again from
  the page would only add load to the thing that is already struggling.
- **It is not quieter.** The operators still get an error — a louder and more accurate one, which
  now names the store rather than blaming your view.
- **It does not pretend to be temporary in the way a "loading" message is.** Nothing announces that
  a database has come back, so the page does not sit there promising something it cannot deliver.
  It tells you plainly that reloading is the thing to do.

A genuine fault in a view is untouched: it still shows the error and the message that explains it,
because that one really is something to look into.
