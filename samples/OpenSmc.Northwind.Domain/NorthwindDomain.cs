namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Collective class referencing all domain types in the Northwind domain.
/// </summary>
public static class NorthwindDomain
{
    /// <summary>
    /// All operational types in the Northwind domain.
    /// </summary>
    public static Type[] OperationalTypes { get; } =
        [typeof(Order), typeof(OrderDetails), typeof(Supplier), typeof(Employee), typeof(Product), typeof(Customer)];

    /// <summary>
    /// All reference data types in the Northwind domain.
    /// </summary>
    public static Type[] ReferenceDataTypes { get; } = [typeof(Category), typeof(Region), typeof(Territory)];

}
