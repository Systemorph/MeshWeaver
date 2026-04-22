---
Name: NuGet Packages in Node Types
Category: Documentation
Description: Reference any NuGet package from a node type's Source/*.cs file using the #r "nuget:..." directive. No redeploy, no SDK on the container.
---

When a node type needs a library that isn't already referenced by the portal — statistics, charting, PDF, a cloud SDK — you don't want to redeploy. Add a `#r "nuget:..."` directive at the top of any file under the node type's `Source/` folder and the compiler restores the package in-process before compiling.

This works for both **node type compilation** (C# sources under `Source/`) and **interactive markdown** code cells (see [NuGet Packages](NugetPackages)). The same resolver handles both.

## The directive

At the top of any `.cs` file under `Source/`, before `using` statements:

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

Always pin a specific version. `#r "nuget:MathNet.Numerics"` without a version resolves "latest" at compile time — that makes your node type non-reproducible and may pick up a breaking change.

## End-to-end example

A complete node type that uses MathNet.Numerics to compute the inverse of a 2×2 matrix and renders the result.

### 1. Folder layout

```
samples/Graph/Data/
  MathDemo/
    Matrix.json              # NodeType definition
    Matrix/
      Source/
        Matrix.cs            # Content record — references MathNet
        MatrixLayoutAreas.cs # Layout area that invokes MathNet
```

### 2. `Source/Matrix.cs`

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

### 3. `Source/MatrixLayoutAreas.cs`

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

Pin the same package version across every `Source/` file that uses it — each file is resolved independently, so mismatched versions would produce conflicting assemblies.

### 4. `Matrix.json`

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

## See it run

The deployed sample lives at `MathDemo/Matrix/Example`. Its `Inverse` layout area — rendered by `MatrixLayoutAreas.Inverse` compiled from `Source/` with the `#r "nuget:MathNet.Numerics, 5.0.0"` directive — embeds directly below:

@MathDemo/Matrix/Example/Inverse

And here is the equivalent interactive-markdown cell — same NuGet directive, same MathNet call, executed by the kernel every time this page loads:

```csharp --render MatrixInverseDemo --show-code
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
```

Both routes go through the same `NuGetAssemblyResolver` — the node-type compilation path for the layout-area embed, and the kernel preprocessor for the code cell. On a fresh replica the first of the two pays the single MathNet restore; the second hits the in-memory cache instantly.

## Caching

The resolver keeps an in-memory cache keyed by the sorted `(Id, VersionRange)` tuple. Within a single portal process every subsequent compilation that names the same packages reuses the already-resolved assembly list — no repeat HTTP calls. Across restarts, the NuGet package folder on disk (`$NUGET_PACKAGES`, default `~/.nuget/packages`) provides the second level of caching; only a fresh replica on a fresh ACA node triggers a real download.

## Deployment — no .NET SDK required

The resolver is built on the public `NuGet.Protocol` / `NuGet.Packaging` / `NuGet.Resolver` libraries. It does not invoke `dotnet restore`, does not need MSBuild, and runs on the plain `mcr.microsoft.com/dotnet/aspnet` runtime image. ACA needs only:

- Outbound HTTPS to `api.nuget.org` (the default egress policy allows it).
- A writable cache directory. The Aspire AppHost sets `NUGET_PACKAGES=/tmp/nuget-cache` on the portal resource; this is ephemeral per replica, which is fine because in-memory cache + first-use restore is fast.

## Transitive dependencies at runtime

NuGet packages often pull in transitive assemblies that aren't referenced by your code at compile time but are loaded later by the main package. The node's `AssemblyLoadContext` is extended with a probing directory list pointing at every `lib/<tfm>/` folder of every resolved package, so those loads succeed without extra configuration.

## Failure modes

- **Unknown package id** — compilation fails with a NuGet error naming the id. Typo check and retry.
- **No matching version** — same, the error lists the versions that were available on the feed.
- **Network blocked** — timeout from the NuGet protocol. Verify the ACA egress policy and that the `NUGET_PACKAGES` directory is writable.
- **Package ships only full-framework assemblies** — the resolver picks the nearest compatible TFM via `FrameworkReducer.GetNearest`. If the package has no `.NET Standard` or `.NET 8/10` asset, no DLLs are returned and compilation fails with "type not found". Pick a different package or version.

## Related

- [NuGet Packages](NugetPackages) — same `#r "nuget:..."` directive inside interactive markdown code cells.
- [Creating Node Types](CreatingNodeTypes) — the base walkthrough for defining content types and layout areas.
- [Interactive Markdown](InteractiveMarkdown) — how code cells execute.
