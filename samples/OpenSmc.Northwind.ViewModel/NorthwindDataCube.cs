using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;
using OpenSmc.Northwind.Domain;

namespace OpenSmc.Northwind.ViewModel
{
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

        [NotVisible]
        public DateTime OrderDate { get; init; }

        [NotVisible]
        public DateTime RequiredDate { get; init; }

        [NotVisible]
        public DateTime ShippedDate { get; init; }

        [NotVisible]
        public int ShipVia { get; init; }

        [NotVisible]
        public string ShipCountry { get; init; }

        [NotVisible]
        public decimal Freight { get; init; }

        [NotVisible]
        public string Product { get; init; }

        public double Amount => UnitPrice * Quantity * (1 - Discount);

        [NotVisible]
        public double UnitPrice { get; init; }

        [NotVisible]
        public int Quantity { get; init; }

        [NotVisible]
        public double Discount { get; init; }

        [NotVisible]
        public string QuantityPerUnit { get; init; }

        [NotVisible]
        public short UnitsInStock { get; init; }

        [NotVisible]
        public short UnitsOnOrder { get; init; }

        [NotVisible]
        public short ReorderLevel { get; init; }

        [NotVisible]
        public string Discontinued { get; init; }
    }
}
