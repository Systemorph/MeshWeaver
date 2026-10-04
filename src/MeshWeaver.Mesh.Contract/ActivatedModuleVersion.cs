namespace MeshWeaver.Mesh;

/// <summary>
/// One module this process was booted with, stated as the PACKAGE release it is — registered as an
/// enumerable DI singleton by the portal boot, one per effective module (MeshWeaver.Plugins#2715).
///
/// <para><b>Why it exists.</b> In-mesh sources compile against the module assemblies the process
/// has LOADED. A synced package that declares <c>requires: ["AI@^1.20.0"]</c> means "my sources call
/// AI 1.20", and the import that writes those sources needs to know which AI THIS instance runs to
/// hold them when it is older (<c>ModuleSyncDecision.DecideAgainstRunningModules</c> in
/// MeshWeaver.GitSync). The loaded assembly cannot say: a module's informational version is the
/// PLATFORM's (MeshWeaver#3732), not its package's. The boot knows, because it chose the copy —
/// a landed store generation records its package version, and the image's own copy carries the
/// version stamped at image build (<c>ImageModuleSeed</c>, MeshWeaver#6044).</para>
///
/// <para>🚨 It describes what the boot HANDED THE LOADER. A copy the loader then refused, or one it
/// fell back from, is reported by <see cref="IncompatibleModule"/> / <see cref="FallbackModule"/>,
/// which a reader that needs the exact loaded generation consults beside this.</para>
/// </summary>
/// <param name="Module">The module's assembly simple name (<c>MeshWeaver.AI</c>).</param>
/// <param name="Package">The package id that ships it (<c>AI</c>), or null when unrecorded.</param>
/// <param name="Version">The package release of the copy booted (<c>1.20.3</c>), or null when
/// unrecorded — which judges nothing.</param>
public sealed record ActivatedModuleVersion(string Module, string? Package, string? Version);
