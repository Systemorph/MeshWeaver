---
Name: Release history stops being erased on every import
Category: Fix
Description: Built-in areas of the platform kept only their newest release record — every earlier one was deleted the next time the content was refreshed. The whole history is kept now.
Icon: Sparkle
Order: -20260813
---

# Release history stops being erased on every import

Every time a live-compiled area of the platform builds successfully, it writes down a small record
of that build: when it happened, what went into it, and the notes its author wrote. Those records
are the release history you see on the area's page.

For the parts of the platform that ship with the product — the documentation, and the built-in
catalogs — that history was being wiped. Only the newest record ever survived; the moment the
shipped content was refreshed, everything older vanished. Areas outside those built-in sections kept
their full history, so the difference was easy to miss unless you happened to look at both.

The cause was a disagreement about what "missing" means. Refreshing shipped content works by
comparing what is in the product against what is in the platform, and removing anything the product
no longer carries — that is what keeps a deleted page from lingering forever. A release record can
never be in the product, though: it does not exist until the platform compiles the area, minutes or
days after the product was built. So every record looked like something that had been deleted, and
was duly removed.

A related version of this was fixed earlier for content synchronised from a repository, but the fix
had to be opted into and the built-in content never did — so it kept deleting its own history, and
the failed cleanup attempts showed up as errors nobody could place.

The platform now treats a release record as something *it* produced rather than something the
product supplied, so its absence from the product is no longer read as a deletion. This can only
result in fewer things being removed, never more: ordinary content that genuinely disappeared from
the product is still cleaned up exactly as before, and a page whose name merely resembles a release
is untouched.
