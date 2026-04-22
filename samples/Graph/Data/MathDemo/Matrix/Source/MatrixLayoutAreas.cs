// <meshweaver>
// Id: MatrixLayoutAreas
// DisplayName: Matrix Layout Areas
// </meshweaver>
#r "nuget:MathNet.Numerics, 5.0.0"

using MathNet.Numerics.LinearAlgebra;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

public static class MatrixLayoutAreas
{
    public static LayoutDefinition AddMatrixLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView("Inverse", Inverse);

    public static UiControl Inverse(LayoutAreaHost host, RenderingContext _)
    {
        var m = Matrix<double>.Build.DenseOfArray(new[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 }
        });
        var inv = m.Inverse();
        return Controls.Markdown($"""
            **Matrix**
            ```
            {m}
            ```
            **Inverse**
            ```
            {inv}
            ```
            """);
    }
}
