---
Name: NuGet Packages in Node Types
Category: Documentation
Description: Reference any NuGet package from a node type's Source/*.cs file using the #r "nuget:..." directive — no redeploy, no SDK on the container.
---

Need a statistics library, a PDF renderer, or a cloud SDK in your node type? You don't have to redeploy the portal to get it. Drop a `#r "nuget:..."` directive at the top of any `.cs` file under the node type's `Source/` folder and the compiler restores the package in-process before building.

This mechanism works in two places:

| Where | What happens |
|---|---|
| `Source/*.cs` files in a node type | Package resolves at node-type compilation time |
| Interactive markdown code cells | Package resolves before the kernel compiles the cell |

Both routes go through the same `NuGetAssemblyResolver`. See also [NuGet Packages](/Doc/DataMesh/NugetPackages) for the interactive-markdown side of the story.
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="20" y="30" width="160" height="54" rx="10" fill="#1e88e5"/>
  <text x="100" y="52" text-anchor="middle" fill="#fff" font-weight="bold">Source/*.cs</text>
  <text x="100" y="70" text-anchor="middle" fill="#fff" font-size="11">#r "nuget:Pkg, 1.0"</text>
  <rect x="20" y="130" width="160" height="54" rx="10" fill="#5c6bc0"/>
  <text x="100" y="152" text-anchor="middle" fill="#fff" font-weight="bold">Interactive</text>
  <text x="100" y="170" text-anchor="middle" fill="#fff" font-size="11">Markdown cell</text>
  <line x1="180" y1="57" x2="278" y2="100" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="180" y1="157" x2="278" y2="115" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="280" y="80" width="180" height="60" rx="10" fill="#43a047"/>
  <text x="370" y="105" text-anchor="middle" fill="#fff" font-weight="bold">NuGetAssembly</text>
  <text x="370" y="123" text-anchor="middle" fill="#fff" font-weight="bold">Resolver</text>
  <line x1="460" y1="110" x2="518" y2="80" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="460" y1="110" x2="518" y2="170" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="520" y="50" width="160" height="54" rx="10" fill="#f57c00"/>
  <text x="600" y="72" text-anchor="middle" fill="#fff" font-weight="bold">In-Memory Cache</text>
  <text x="600" y="90" text-anchor="middle" fill="#fff" font-size="11">(Id, VersionRange)</text>
  <rect x="520" y="150" width="160" height="54" rx="10" fill="#26a69a"/>
  <text x="600" y="172" text-anchor="middle" fill="#fff" font-weight="bold">NuGet Feed</text>
  <text x="600" y="190" text-anchor="middle" fill="#fff" font-size="11">api.nuget.org</text>
  <line x1="600" y1="204" x2="600" y2="238" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="520" y="240" width="160" height="54" rx="10" fill="#8e24aa"/>
  <text x="600" y="262" text-anchor="middle" fill="#fff" font-weight="bold">Disk Package</text>
  <text x="600" y="280" text-anchor="middle" fill="#fff" font-size="11">~/.nuget/packages</text>
  <line x1="460" y1="110" x2="518" y2="267" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" stroke-dasharray="4 3" marker-end="url(#arr)"/>
  <rect x="280" y="245" width="180" height="54" rx="10" fill="#e53935"/>
  <text x="370" y="267" text-anchor="middle" fill="#fff" font-weight="bold">AssemblyLoad</text>
  <text x="370" y="285" text-anchor="middle" fill="#fff" font-weight="bold">Context</text>
  <line x1="520" y1="267" x2="462" y2="267" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="370" y="18" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="12">NuGet resolution flow — dashed = cache hit skips feed</text>
</svg>

*NuGet assembly resolution: both Source files and interactive cells share one `NuGetAssemblyResolver`; resolved assemblies are loaded into a per-node `AssemblyLoadContext`.*

---

## The directive

Place `#r "nuget:..."` at the very top of any `.cs` file, before `using` statements:

```csharp
#r "nuget:MathNet.Numerics, 5.0.0"

using MathNet.Numerics.LinearAlgebra;

public record MatrixDemo
{
    public double[,] Data { get; init; } = { { 1, 2 }, { 3, 4 } };

    public double Determinant() =>
        Matrix<double>.Build.DenseOfArray(Data).Determinant();
}
```

> **Always pin a specific version.** `#r "nuget:MathNet.Numerics"` without a version resolves "latest" at compile time — that makes your node type non-reproducible and may silently pick up a breaking change.

---

## End-to-end example

The following walks through a complete node type that uses MathNet.Numerics to compute the inverse of a 2×2 matrix and renders the result as a layout area.

### Folder layout

```
samples/Graph/Data/
  MathDemo/
    Matrix.json              # NodeType definition
    Matrix/
      Source/
        Matrix.cs            # Content record — references MathNet
        MatrixLayoutAreas.cs # Layout area that calls MathNet
```

### `Source/Matrix.cs`

```csharp
// <meshweaver>
// Id: Matrix
// DisplayName: Matrix
// </meshweaver>
#r "nuget:MathNet.Numerics, 5.0.0"

using MathNet.Numerics.LinearAlgebra;
using MeshWeaver.Domain;

public record Matrix
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public double A11 { get; init; } = 1;
    public double A12 { get; init; } = 2;
    public double A21 { get; init; } = 3;
    public double A22 { get; init; } = 4;

    public double Determinant()
    {
        var m = Matrix<double>.Build.DenseOfArray(new[,]
        {
            { A11, A12 },
            { A21, A22 }
        });
        return m.Determinant();
    }
}
```

### `Source/MatrixLayoutAreas.cs`

```csharp
// <meshweaver>
// Id: MatrixLayoutAreas
// DisplayName: Matrix Layout Areas
// </meshweaver>
#r "nuget:MathNet.Numerics, 5.0.0"

using MathNet.Numerics.LinearAlgebra;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

public static class MatrixLayoutAreas
{
    public static MessageHubConfiguration AddMatrixLayoutAreas(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout.WithView("Inverse", Inverse));

    public static UiControl Inverse(LayoutAreaHost host, RenderingContext _)
    {
        var m = Matrix<double>.Build.DenseOfArray(new[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 }
        });
        var inv = m.Inverse();
        return Controls.Markdown($"""
            **Matrix:**
            ```
            {m}
            ```
            **Inverse:**
            ```
            {inv}
            ```
            """);
    }
}
```

> Pin the same package version across every `Source/` file that uses it. Each file is resolved independently, so mismatched versions produce conflicting assemblies.

### `Matrix.json`

```json
{
  "id": "Matrix",
  "namespace": "MathDemo",
  "name": "Matrix",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "namespace": "MathDemo",
    "displayName": "Matrix",
    "configuration": "config => config
      .WithContentType<Matrix>()
      .AddLayout(layout => layout
        .AddMatrixLayoutAreas()
        .WithDefaultArea(\"Inverse\"))"
  }
}
```

---

## See it run

The deployed sample lives at `MathDemo/Matrix/Example`. Its `Inverse` layout area — compiled from `Source/` with the `#r "nuget:MathNet.Numerics, 5.0.0"` directive — is embedded directly below:

@MathDemo/Matrix/Example/Inverse

Here is the equivalent interactive-markdown cell: same NuGet directive, same MathNet call, executed by the kernel every time this page loads:

````csharp --render MatrixInverseDemo --show-code
#r "nuget:MathNet.Numerics, 5.0.0"
using MathNet.Numerics.LinearAlgebra;

var m = Matrix<double>.Build.DenseOfArray(new double[,] { { 1, 2 }, { 3, 4 } });
var inv = m.Inverse();
Controls.Markdown($"""
**Matrix**
```
{m}
```
**Inverse**
```
{inv}
```
**Determinant:** {m.Determinant()}
""")
````

On a fresh replica the first of the two pays the single MathNet restore; the second hits the in-memory cache instantly. Both use the same `NuGetAssemblyResolver` under the hood.

---

## Caching

The resolver maintains an in-memory cache keyed by the sorted `(Id, VersionRange)` tuple. Within a single portal process, every subsequent compilation that names the same packages reuses the already-resolved assembly list — no repeat HTTP calls.

Across restarts, the NuGet package folder on disk (`$NUGET_PACKAGES`, default `~/.nuget/packages`) provides a second caching layer. Only a fresh replica on a fresh ACA node triggers a real download.

---

## Deployment — no .NET SDK required

The resolver is built on the public `NuGet.Protocol`, `NuGet.Packaging`, and `NuGet.Resolver` libraries. It does not invoke `dotnet restore`, does not need MSBuild, and runs on the plain `mcr.microsoft.com/dotnet/aspnet` runtime image. ACA needs only:

- **Outbound HTTPS** to `api.nuget.org` (the default egress policy allows this).
- **A writable cache directory.** The Aspire AppHost sets `NUGET_PACKAGES=/tmp/nuget-cache` on the portal resource. This is ephemeral per replica, which is fine — in-memory cache plus first-use restore is fast.

---

## Transitive dependencies at runtime

NuGet packages often pull in transitive assemblies that aren't referenced by your code at compile time but are loaded later by the main package. The node's `AssemblyLoadContext` is extended with a probing directory list pointing at every `lib/<tfm>/` folder of every resolved package, so those loads succeed without extra configuration.

---

## Failure modes

| Symptom | Cause | Fix |
|---|---|---|
| NuGet error naming the package id | Unknown package id — likely a typo | Check the exact id on nuget.org and retry |
| NuGet error listing available versions | No matching version | Pin a version that exists on the feed |
| Timeout from NuGet protocol | Network blocked | Verify ACA egress policy and that `NUGET_PACKAGES` is writable |
| "type not found" at compilation | Package has no .NET Standard / .NET 8/10 asset | The resolver uses `FrameworkReducer.GetNearest`; if no compatible TFM exists, no DLLs are returned — pick a different package or version |

---

## Related

- [NuGet Packages](/Doc/DataMesh/NugetPackages) — the same `#r "nuget:..."` directive inside interactive markdown code cells.
- [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes) — the base walkthrough for defining content types and layout areas.
- [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown) — how code cells execute.
