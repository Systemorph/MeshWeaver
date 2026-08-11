---
Name: Every page shows its own icon
Category: Feature
Description: A node page now publishes the node's own icon to the web, so link previews of it — the portal's own preview cards and Slack, Teams or LinkedIn unfurls alike — show that node's mark instead of the same portal logo on every card.
Icon: ImageSparkle
Order: -20260811
---

# Every page shows its own icon

A grid of preview cards is supposed to help you tell pages apart. Ours did the
opposite: eight modules, eight identical MeshWeaver logos. The same happened
wherever a page was shared — a link dropped into Slack, Teams or LinkedIn unfurled
with the portal's mark, never the page's.

The cause was mundane and completely invisible from inside the app. Every page of
the portal served one site-wide icon, declared once in the shared layout. Each
node already had a distinctive icon, and the portal rendered it everywhere
*internally* — in the tree, in menus, on cards — but that icon never reached the
one place the outside world looks: the page's own head.

Now it does. A node page publishes **its own icon** through the standard
`<link rel="icon">` channel, alongside the title, description and share image it
already published. Nothing had to learn anything about MeshWeaver to benefit:

- the portal's own **preview cards** now show each target's mark;
- **Slack / Teams / LinkedIn** unfurls show it too;
- and the **browser tab** shows it when you open the page.

It is the icon on the node, and only that. Nothing is invented: a node that
carries no icon of its own keeps the portal favicon, which is the honest answer
for a page with no mark to show. An icon drawn as an inline graphic travels
unchanged; one stored as a file resolves through the same access-controlled route
the app uses, so a private space's mark stays private.

One related correction: when a page declares its own icon and the site declares
one too, the page's now wins — matching how browsers resolve it. Without that, a
page could never override the site-wide mark, which is exactly how every card
ended up wearing the same logo.
