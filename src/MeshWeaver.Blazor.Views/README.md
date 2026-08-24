# MeshWeaver.Blazor.Views

The DEFAULT view pack: the standard Blazor renderer for every framework control — buttons,
markdown, the Monaco editors, grids, navigation, dialogs, the file explorer — plus the standard
skin views and the whole control→view map (`DefaultFormatting`), factored out of the base
`MeshWeaver.Blazor` pack onto the view-pack seam (the EntityViews/Analysis precedent).

The base pack keeps only what every Blazor host needs: the `BlazorView`/`SkinnedView`/`ListBase`/
`InputBase`/`FormComponentBase` base classes, the area-hosting machinery (`LayoutAreaView`,
`NamedAreaView`, `DispatchView`), the app-shell pieces its own pages use, and the escaped-HTML
fallback slot. A host without THIS pack renders every standard control through that fallback —
which is why the portals list the DLL under `Modules:Assemblies`.

- **Activate**: list `MeshWeaver.Blazor.Views.dll` under `Modules:Assemblies`
  (`ViewsViewPackModuleAttribute` folds `AddDefaultViews()`), or call
  `configuration.AddDefaultViews()` from a compiled host.
- **Namespaces**: the components keep their original `MeshWeaver.Blazor.Components` /
  `.Components.Monaco` / `.FileExplorer` namespaces (`RootNamespace` pin) so every `typeof()`,
  consumer `@using`, and the react parity ratchet stayed valid across the move.
- **Assets**: collocated JS/CSS serve from `_content/MeshWeaver.Blazor.Views/`.
