---
Name: Search engines can now read every public page
Category: Fix
Description: A public page — a course lesson, a documentation chapter, a plugin's cover — reached a search engine with its title and description but no text, and the sitemap named only the top of each tree. Every public page now ships its content in the first response, the sitemap lists every page a stranger may open, and a deployment can name one public host that all of it is filed under.
Icon: Globe
Order: -20260911
---

# Search engines can now read every public page

Some pages here are public on purpose: course covers and their free lessons, the platform
documentation, the plugin store, the showcases. Each of them already told a search engine what it
was — a title, a description, a share card — and then handed it an empty page, because the text
only arrived once the browser had opened a live connection, which a crawler never does. Measured on
the public instance on 2026-09-11: not one of its pages was in Google's index.

Three things change.

**The page arrives with its text.** A public page now carries its content in the very first
response, rendered on the server from the same source a signed-in visitor sees. The rule that
decides whether a stranger may read a page is unchanged and still decides this: a lesson behind a
paywall is refused before any of its text is produced.

**The sitemap goes all the way down.** It used to name the top of each public tree — the course,
never its lessons; the documentation, never a chapter. It now lists every page below a public root
that a stranger may open, and only those: data records, source files and release markers in the
same tree are not pages and are not listed. **Administration → Published to the web** reads the
same list, so what you see there is what a search engine is told.

**One address per page.** A deployment that serves its public site and its signed-in workspace on
two host names can now say which one is the public one. Every canonical link and every sitemap
entry then names that host, the workspace host tells crawlers to stay out, and a stranger who
follows a shared workspace link to a public page is sent to the public address instead. A
deployment with one host is unchanged.

Also in this change: a `HEAD` request to a page is answered instead of refused, and a URL that does
not exist and falls back to its nearest page is marked so that fallback never competes with the real
page in search results.
