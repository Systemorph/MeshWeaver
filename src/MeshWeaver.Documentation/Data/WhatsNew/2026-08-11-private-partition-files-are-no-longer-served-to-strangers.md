---
Name: Private partition files are no longer served to strangers
Category: Fix
Description: A file uploaded to a private partition could be fetched by anyone with the URL, without signing in. Content files now require exactly the same permission as the node that owns them.
Icon: Sparkle
Order: -20260811
---

# Private partition files are no longer served to strangers

A file stored in a partition's content collection — an upload, an attachment, an image — could be
fetched over `/api/content/{partition}/content/{file}` by a caller who had never signed in, and by
a signed-in caller who held no permission on that partition. Only the URL was needed, and the URL
scheme is entirely predictable.

The route was always meant to be the access-controlled one. It asked the owning node's hub for the
collection's configuration, a request that carries a read-permission requirement, and treated that
as the gate. The gate was real, but one rule quietly satisfied it: user partitions grant read
access to "every signed-in user", and the rule that decided who counted as signed in tested only
that the caller had a name. An unauthenticated caller does have one — it is called `Anonymous` —
so it passed, and with it the whole hub's read check, the collection's configuration, and the file
itself. A caller with no identity at all would have been refused; naming the anonymous caller
correctly is what opened the door.

Two things changed. "Signed in" is now a single shared definition that excludes the anonymous and
public placeholder identities, so no rule can drift into admitting them again. And the content
route now asks the permission evaluator directly, about the node that owns the file, instead of
inheriting the answer from a configuration read that a hub-wide rule can satisfy on its own. For a
logged-out visitor that is the same check the public share-card and crawler pages already use: a
file is served anonymously only where its partition carries an explicit grant for anonymous
readers.

Public pages are unaffected — plugin covers, course landing pages and share cards carry that
explicit grant and keep serving to logged-out visitors. What changes is everything else: a refused
file now answers exactly as a missing one, so the response cannot be used to discover which files
exist, and a permission check that cannot reach a verdict refuses the read rather than allowing it.
