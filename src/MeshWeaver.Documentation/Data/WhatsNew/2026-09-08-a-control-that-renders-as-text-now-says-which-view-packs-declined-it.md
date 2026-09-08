---
Name: A control that renders as text now says which view packs declined it
Category: Fix
Description: When a page shows a control as raw text instead of the real thing, the portal now logs one line naming the control, its skins, the area, the hub, and every view pack that was asked and said no — or that no view pack registered at all. Until now that path was silent.
Icon: Bug
Order: -20260908
---

# A control that renders as text now says which view packs declined it

Occasionally a page shows a control as its own description — a line of text beginning
`StackControl { Id = , Style = … }` where a layout should be. That is the portal's last resort: no
registered view pack accepted the control, so it rendered the control's text, escaped. It is
harmless, and it is also completely silent — nothing on that path said which packs were asked, or
whether any pack had registered on that hub at all, so an operator reading the logs had nothing to
start from.

**Now it logs once per control type per hub**, at Warning, on the `MeshWeaver.Layout.Client.ViewDispatch`
channel:

```
no view map accepted StackControl ($type StackControl, skins [LayoutStackSkin]) in area Workspace
on hub mesh/memex: 3 map(s) registered [MeshWeaver.Blazor.Views:AddDefaultViews,
MeshWeaver.Blazor.EntityViews:AddEntityViews, …] — falling back to the last-resort view
(escaped HTML in the Blazor portal)
```

The line separates the two causes that look identical on screen. `0 map(s) registered — no view
pack applied its HubConfigurations to this hub` means the view packs never reached the hub the page
renders on; a list of owners means they did, and every one of them declined this control — and the
skins and the control's assembly are printed, so a skin no pack knows, or a same-named type loaded
from a second assembly, reads off the line directly.

A page with fifty controls of one kind logs one line, not fifty; a different kind on the same hub
logs again. Nothing changes on screen.

See [UI Extensibility](/Doc/Architecture/UiExtensibility) for how to read the line.
