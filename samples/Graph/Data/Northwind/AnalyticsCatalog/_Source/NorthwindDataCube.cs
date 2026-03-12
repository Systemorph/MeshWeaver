// <meshweaver>
// Id: NorthwindDataCube
// DisplayName: Northwind Data Cube
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Virtual data cube combining Orders, OrderDetails, and Products with enriched dimension names.
/// </summary>
public record NorthwindDataCube()
{
    public NorthwindDataCube(Order order, OrderDetails details, Product product)
        : this()
    {
        OrderId = order.OrderId;
        OrderDetailsId = details.Id;
        Customer = order.CustomerId;
        Employee = order.EmployeeId;
        OrderDate = order.OrderDate;
        OrderMonth = order.OrderDate.ToString("yy-MM");
        OrderYear = order.OrderDate.Year;
        RequiredDate = order.RequiredDate;
        ShippedDate = order.ShippedDate;
        ShipVia = order.ShipVia;
        Freight = order.Freight;
        ShipCountry = order.ShipCountry;
        Product = product.ProductId;
        ProductName = product.ProductName;
        UnitPrice = product.UnitPrice;
        Quantity = details.Quantity;
        Discount = details.Discount;
        Region = order.ShipRegion;
        Supplier = product.SupplierId;
        Category = product.CategoryId;
        QuantityPerUnit = product.QuantityPerUnit;
        UnitsInStock = product.UnitsInStock;
        UnitsOnOrder = product.UnitsOnOrder;
        ReorderLevel = product.ReorderLevel;
        Discontinued = product.Discontinued;
        Amount = product.UnitPrice * details.Quantity * (1 - details.Discount);
    }

    protected NorthwindDataCube(NorthwindDataCube original)
    {
        OrderId = original.OrderId;
        OrderDetailsId = original.OrderDetailsId;
        Customer = original.Customer;
        CustomerName = original.CustomerName;
        Employee = original.Employee;
        EmployeeName = original.EmployeeName;
        OrderDate = original.OrderDate;
        OrderMonth = original.OrderMonth;
        OrderYear = original.OrderYear;
        RequiredDate = original.RequiredDate;
        ShippedDate = original.ShippedDate;
        ShipVia = original.ShipVia;
        Freight = original.Freight;
        ShipCountry = original.ShipCountry;
        Product = original.Product;
        ProductName = original.ProductName;
        UnitPrice = original.UnitPrice;
        Quantity = original.Quantity;
        Discount = original.Discount;
        Region = original.Region;
        RegionName = original.RegionName;
        Supplier = original.Supplier;
        SupplierName = original.SupplierName;
        Category = original.Category;
        CategoryName = original.CategoryName;
        QuantityPerUnit = original.QuantityPerUnit;
        UnitsInStock = original.UnitsInStock;
        UnitsOnOrder = original.UnitsOnOrder;
        ReorderLevel = original.ReorderLevel;
        Discontinued = original.Discontinued;
        Amount = original.Amount;
    }

    [Dimension(typeof(Order))]
    public int OrderId { get; init; }

    [Key]
    [Display(Name = "Count")]
    public int OrderDetailsId { get; init; }

    [NotVisible]
    public string? Customer { get; init; }

    [Dimension(typeof(string), nameof(CustomerName))]
    public string? CustomerName { get; init; }

    [NotVisible]
    public int Employee { get; init; }

    [Dimension(typeof(string), nameof(EmployeeName))]
    public string? EmployeeName { get; init; }

    [NotVisible]
    public int Supplier { get; init; }

    [Dimension(typeof(string), nameof(SupplierName))]
    [Display(Name = "Supplier")]
    public string? SupplierName { get; init; }

    [NotVisible]
    public int Category { get; init; }

    [Dimension(typeof(string), nameof(CategoryName))]
    public string? CategoryName { get; init; }

    [NotVisible]
    public string? Region { get; init; }

    [Dimension(typeof(string), nameof(RegionName))]
    public string? RegionName { get; init; }

    [NotVisible]
    public DateTime OrderDate { get; init; }

    [Dimension(typeof(string), nameof(OrderMonth))]
    [Display(Name = "Month")]
    public string? OrderMonth { get; init; }

    [NotVisible]
    [Dimension(typeof(int), nameof(OrderYear))]
    public int OrderYear { get; init; }

    [NotVisible]
    public DateTime RequiredDate { get; init; }

    [NotVisible]
    public DateTime ShippedDate { get; init; }

    [NotVisible]
    public int ShipVia { get; init; }

    [NotVisible]
    public string? ShipCountry { get; init; }

    [NotVisible]
    public decimal Freight { get; init; }

    [Dimension(typeof(Product))]
    public int Product { get; init; }

    [NotVisible]
    public string? ProductName { get; init; }

    [Display(Name = "Amount")]
    [DisplayFormat(DataFormatString = "{0:C}")]
    public double Amount { get; init; }

    [NotVisible]
    public double UnitPrice { get; init; }

    [NotVisible]
    public int Quantity { get; init; }

    [NotVisible]
    [Dimension(typeof(double), nameof(Discount))]
    public double Discount { get; init; }

    [NotVisible]
    public string? QuantityPerUnit { get; init; }

    [NotVisible]
    public short UnitsInStock { get; init; }

    [NotVisible]
    public short UnitsOnOrder { get; init; }

    [NotVisible]
    public short ReorderLevel { get; init; }

    [NotVisible]
    public string? Discontinued { get; init; }
}
