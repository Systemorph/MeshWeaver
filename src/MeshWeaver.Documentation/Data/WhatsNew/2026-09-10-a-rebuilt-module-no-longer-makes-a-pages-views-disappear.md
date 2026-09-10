---
Name: A rebuilt module no longer makes a page's views disappear
Category: Fix
Description: A page could return successfully and render nothing — every view its type declares simply absent — because a module it uses had been rebuilt. The rebuild is no longer treated as an incompatibility, and a page that genuinely cannot load its code now says so instead of coming up empty.
Icon: Checkmark
Order: -20260910
---

# A rebuilt module no longer makes a page's views disappear

Some pages came up **blank**. Not an error page and not a slow one: the page loaded, reported
success, and every view its type defines — the preview, the editor, the card — was simply *not
there*. Anything generic still rendered, so the page looked like it worked and had nothing on it.

Nothing was broken in the content. The type reported itself as **compiled and healthy** the entire
time. A restart did not help, and neither did opening it again on another day.

The cause was upstream and invisible from the page: a shared building block had been **compiled more
than once**. Compiling the very same code twice produces two builds that are, byte for byte,
different — that is simply how compilation works — and the platform was treating "a different build"
as "a different, possibly incompatible thing". So a page's compiled views were refused, rebuilt
locally on whichever server happened to serve you, and then refused again by the other server. The
two never agreed, and while they disagreed the views were nowhere.

On a portal running two servers this could persist for a full day.

## What changes

**A rebuild is no longer treated as an incompatibility.** A page's compiled views now record *"I
need this building block at version X or newer"* rather than *"I need this exact build of it"*. Two
builds of one building block work together — they always did — so the views are simply used, and the
tug-of-war has nothing left to be about.

**Nothing gets rebuilt at page-open time for this reason any more.** That rebuilding was the whole
mechanism behind the blank page, and it is gone.

**A version that really is too old still refuses, and says which one.** The check did not become
"accept anything": if your installation genuinely carries an older building block than a page's
views were built for, the views are refused exactly as before, and the reason now names the block
and both versions instead of a single generic sentence.

**A page that cannot load its code shows a diagnosis instead of coming up empty.** Where the
platform knows a page has compiled views but cannot find them on the server answering you, it now
renders the same *"this could not be loaded"* card the rest of the platform uses, with the reason —
and it repairs itself and reloads the page as soon as the views become available. Previously it
silently fell back to a bare page, which is what made this look like "the feature was never there"
rather than "something went wrong".

## What this does not change

**Nothing else about how a page's code is validated has loosened.** A page's views are still refused
when the platform itself has moved underneath them, when their source has changed, or when they
reference something this installation does not have at all. Only the "which build of the module"
question — the one where both answers were always fine — stopped refusing.

**Installing and updating modules works exactly as it did.** Whether a module *loads* is still
decided by measuring it against the running platform, never by comparing version strings.

**"I could not check" is never read as "this is fine".** Where the two sides cannot be compared at
all, the views are refused and the page's code is rebuilt — the safe direction — rather than being
accepted on the assumption that silence means agreement.
