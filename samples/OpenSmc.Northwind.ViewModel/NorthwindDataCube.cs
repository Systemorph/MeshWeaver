
using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;
using OpenSmc.Northwind.Domain;

namespace OpenSmc.Northwind.ViewModel;

public record NorthwindDataCube()
{
    public NorthwindDataCube(Order order, OrderDetails details, Product product) 
        : this()
    {
        OrderId = order.OrderId;
        Customer = order.CustomerId;
        Employee = order.EmployeeId;
        OrderDate = order.OrderDate;
        RequiredDate = order.RequiredDate;
        ShippedDate = order.ShippedDate;
        ShipVia = order.ShipVia;
        Freight = order.Freight;
        ShipCountry = order.ShipCountry;
        Product = product.ProductName;
        UnitPrice = details.UnitPrice;
        Quantity = details.Quantity;
        Discount = details.Discount;
        Region = order.ShipRegion;
        Supplier = product.SupplierId;
        ShipName = order.ShipName;
        ShipAddress = order.ShipAddress;
        ShipCity = order.ShipCity;
        ShipPostalCode = order.ShipPostalCode;
        ShipCountry = order.ShipCountry;
        Category = product.CategoryId;
        QuantityPerUnit = product.QuantityPerUnit;
        UnitsInStock = product.UnitsInStock;
        UnitsOnOrder = product.UnitsOnOrder;
        ReorderLevel = product.ReorderLevel;
        Discontinued = product.Discontinued;


    }

    [property: Key]
    public int OrderId { get; init; }
    [property: Dimension(typeof(Customer))]
    public string Customer { get; init; }
    [property: Dimension(typeof(Employee))]
    public int Employee { get; init; }
    [Dimension(typeof(Supplier))]
    public int Supplier { get; init; }

    [Dimension(typeof(Category))]
    public int Category { get; init; }

    [property: Dimension(typeof(Region))]
    public string Region { get; init; }


    public DateTime OrderDate { get; init; }
    public DateTime RequiredDate { get; init; }
    public DateTime ShippedDate { get; init; }

    public int ShipVia { get; init; }

    public string ShipCountry { get; init; }
    public decimal Freight { get; init; }

    public string Product { get; init; }
    public double UnitPrice { get; init; }
    public int Quantity { get; init; }
    public double Discount { get; init; }
    public string ShipName { get; init; }
    public string ShipAddress { get; init; }
    public string ShipCity { get; init; }
    public string ShipPostalCode { get; init; }
    public string QuantityPerUnit { get; init; }
    public short UnitsInStock { get; init; }
    public short UnitsOnOrder { get; init; }
    public short ReorderLevel { get; init; }
    public int Discontinued { get; init; }


}
