---
Name: A Zero-Root Sitemap Is an Assertion
Category: Architecture
Description: The public sitemap projected a tri-state permission gate onto a bool on the stated grounds that omitting one undecidable page "states nothing" — true of one page and false of all of them, because omitting every root produces a 200 that says this deployment publishes nothing. Why a partial answer stays a 200 and only an empty one is withheld, and why nine occurrences over 27 hours left no diagnostic at all.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a15 15 0 0 1 0 18a15 15 0 0 1 0-18"/><path d="m5 19 14-14"/></svg>
---

# A Zero-Root Sitemap Is an Assertion

`/sitemap.xml` is the list of every page a logged-out visitor may open. It is built by asking the
mesh for the candidate partition roots and then asking the **anonymous permission gate** about each
one; the roots that pass contribute themselves and their page-shaped descendants.

For roughly a day the synthetic probe against `memex.meshweaver.cloud` failed 9 times in 100 runs
with a message worth reading twice:

> `memex.meshweaver.cloud served a well-formed sitemap that declares ZERO public roots. Either the
> portal publishes nothing anonymously or SeoEndpoints' candidate query came back empty; either
> way this probe checked no content and must not read green.`

Not a 5xx, not a timeout, not a malformed body. A **correct** document, saying the portal publishes
nothing. Measured in the same minute, the same host served 1,819 URLs.

## Zero is the only answer that asserts

The gate already answers a tri-state — granted, denied, or **undetermined**, the last one meaning
the permission fold reached no verdict. `AnonymousGate.AllowAnonymous` is the documented **lossy**
projection of it onto a bool, and its own remarks draw the boundary precisely:

> Keep using this only where "unknown" and "not public" lead to the SAME correct action and nothing
> is asserted to a human — omitting a page from the sitemap, withholding SEO metadata. The moment
> the answer produces a redirect, **a status code** or a message, switch to `Evaluate` and branch
> on `IsUndetermined` first.

The sitemap used the bool, and carried a comment justifying it: *"a root the gate cannot decide on
is OMITTED, which is the same action as 'not public' and the fail-closed one. Omission states
nothing."*

**That reasoning is correct for one root and wrong for all of them.** It is a composition failure,
not a logic error: N omissions that each state nothing compose into a document that states
everything, because **the roots ARE the sitemap**. Drop one of a thousand and the sitemap is a hint
that lost a URL until the next crawl — which is squarely within what a sitemap promises. Drop all
of them and what is served is a census: *nothing here is public.*

So the rule the code now applies is deliberately narrow, and lives in one predicate:

```csharp
public bool AssertsWhatItDidNotCheck => Pages.Count == 0 && Undecided is not null;
```

| pages | enumeration | answer | why |
|---|---|---|---|
| some | decided | **200** | a census |
| some | one root undecided | **200** | partial, and a sitemap never promised completeness |
| none | decided | **200**, empty `urlset` | the portal really does publish nothing — that IS the truth |
| none | undecided | **503** + `Retry-After` | zero would assert something nothing established |

The third row is why the rule is not "never serve an empty sitemap": that would turn every private
deployment into a permanent outage on this route, and would stop the probe from ever failing for the
real reason. The fourth is the whole fix.

**503 is not a downgrade of the old behaviour, it is the honest form of it.** The header comment the
change removed read *"fail-open to an empty sitemap — a mesh hiccup must never turn into a 500 for a
crawler"*, and the instinct was right: a crawler should not see a server error over a transient. But
503 with `Retry-After` is exactly the wire's phrase for *ask again later*, and a crawler acts on it
correctly. A **200 declaring zero roots** is the one response it cannot tell from the truth.

## The half that logged nothing

Underneath the gate sat two blanket sinks:

```csharp
.Timeout(TimeSpan.FromSeconds(20))
.Catch<IReadOnlyList<PublishedPage>, Exception>(_ => Observable.Return([]));
...
.Catch<string, Exception>(_ => Observable.Return(Render(baseUrl, [])));
```

Both discard the exception into `_ =>`. A timed-out enumeration, a faulted candidate query and a
completed census therefore produced the **same value**, and nothing anywhere recorded which had
happened — so nine failures over 27 hours left not one line naming a cause. That is the defect the
issue was actually blocked on: it could be measured to the second and diagnosed not at all.

Both sinks are gone. The timeout stays — a 20 s cap at an HTTP edge is a legitimate edge bound, and
the bound was never the problem; what was wrong is that spending it and finishing produced the same
answer. It now faults, `SitemapUnavailable` logs it by name, and the route answers 503.

One catch is deliberately kept: if listing the pages *below* an admitted root fails, that root is
still published alone. That degradation is bounded — it always emits at least the root, so it can
never produce the zero-root document — but it was silent, so it now warns naming the root. A sitemap
that quietly lost a course's chapters used to leave nothing to read either.

## What the measurement actually showed, and what it did not

The obvious hypothesis was that the permission fold had gone undetermined. `AnonymousGate.Evaluate`
logs a warning on exactly that outcome, naming the path and the classifier's reason, and that line is
present in the image the instance runs — so it is checkable.

Over 24 hours of `memex-cloud` logs, `reached NO verdict` returned **zero lines**. A zero has two
causes, so the denominator was measured separately: the namespace does reach Loki, and the matched
control lines bracket **18:20Z–21:03Z on 2026-09-18**, which contains the most recent probe failure
at **18:26:48Z**. For that occurrence, with coverage, the gate warning did not fire.

**So the undetermined gate is not what fired that day** — the remaining candidates are the 20 s
timeout or a faulted query, which is to say: the half that logged nothing. The change does not claim
to have identified the transient. It makes all three causes name themselves the next time, and stops
any of them from being published as a census. Coverage older than 18:20Z was never established, so
the other eight occurrences remain unattributed.

## The same rule for the human view

`PublishedSettingsTab` renders the same enumeration for an administrator — "two views, one truth" —
and inherited the same lie: an empty grid reads as *nothing on this deployment is public*. It now
applies the identical predicate and says the surface could not be read, in both shipped languages,
rather than drawing an empty table.

## Related

- [Access Control](/Doc/Architecture/AccessControl) — the tri-state and why undetermined is not denied
- [A Census That Counts Must Name](/Doc/Architecture/ACensusThatCountsMustName) — the sibling
  failure: a count whose identity was dropped one call before publication, which also reads as clean
- [Public Web Presence](/Doc/Architecture/PublicWebPresence) — what the public host serves and why
