---
Name: Approvals ship as a node-native package
Category: WhatsNew
Description: The approval workflow left the platform build — it is now two NodeTypes the mesh compiles live, installed and removed from the Store like any other package.
---

# Approvals are a package now

Approval workflows used to be a compiled platform module (`MeshWeaver.Approvals`, listed under
`Modules:Assemblies`) that registered a form, an inline section and a menu entry onto **every
per-node hub of the mesh**. They now ship as the node-native **Approvals** package: two NodeTypes
the mesh compiles live from their own `Source/`, with no assembly in the platform image and no
restart to install or remove.

## What changed for you

- **Nothing, if you use approvals.** The Request Approval entry, the inline approvals section on a
  document page, and the Approve / Reject views all work as before — provided the Approvals package
  is installed (it is pre-installed).
- **The feature is now optional in the honest sense.** Uninstalling the package removes the UI
  cleanly; installing it adds the UI immediately, with no image roll.
- **Your data is untouched.** Approvals are still `{document}/_Approval/{id}` satellites in the
  partition's `annotations` table. The `Approval` record and that mapping stay platform-level, so
  stored approvals keep deserializing and routing regardless of what is installed.

## What made it possible

Two platform seams did the work the module used to do by force:

- **A `UiContribution` node** contributes the *Request Approval* menu entry as DATA (#1645). Its
  `Href` may now carry a **`{node}`** token, substituted with the node whose menu is rendered — so
  a contributed entry can open a surface the plugin serves from its OWN workspace, which is the only
  shape available to a package that cannot register an area onto another type's hub.
- **The markdown overview probes and delegates.** Instead of asking a configuration marker whether
  approvals are enabled, it asks the mesh whether the package's desk exists — one bounded query-index
  probe, the same shape the Versions page uses for the Collaboration package — and embeds the
  package's own area. No package, no section, no cost.

## For content authors

`MeshWeaver.Graph.ApprovalExtensions.AddApprovals()` and the rest of the pre-extraction legacy
surface are **gone**: nothing needs per-hub registration any more. In-mesh sources that still call
it simply drop the call.
