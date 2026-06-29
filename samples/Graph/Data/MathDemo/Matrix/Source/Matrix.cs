// <meshweaver>
// Id: Matrix
// DisplayName: Matrix Data Model
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

    public double Determinant() =>
        Matrix<double>.Build
            .DenseOfArray(new[,] { { A11, A12 }, { A21, A22 } })
            .Determinant();
}
