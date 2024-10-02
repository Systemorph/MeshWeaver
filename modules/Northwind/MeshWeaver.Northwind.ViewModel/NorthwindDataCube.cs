using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel
{
    /// <summary>
    /// Defines a data structure for aggregating and analyzing data across multiple dimensions within the Northwind trading application. This record encapsulates detailed information about orders, their details, and associated products.
    /// </summary>
    public record NorthwindDataCube()
    {
        /// <summary>
        /// Initializes a new instance of the NorthwindDataCube with specified order, order details, and product information.
        /// </summary>
        /// <param name="order">The order information.</param>
        /// <param name="details">The details of the order, including unit price, quantity, and discount.</param>
        /// <param name="product">The product information, including name, supplier, category, and stock details.</param>
        public NorthwindDataCube(Order order, OrderDetails details, Product product)
            : this()
        {
            OrderId = order.OrderId;
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
            ShipCountry = order.ShipCountry;
            Category = product.CategoryId;
            QuantityPerUnit = product.QuantityPerUnit;
            UnitsInStock = product.UnitsInStock;
            UnitsOnOrder = product.UnitsOnOrder;
            ReorderLevel = product.ReorderLevel;
            Discontinued = product.Discontinued;
            Amount = product.UnitPrice * details.Quantity * (1 - details.Discount);
        }

        /// <summary>
        /// Initializes a new instance of the NorthwindDataCube with values taken from another existed NorthwindDataCube instance.
        /// </summary>
        /// <param name="original">Original NorthwindDataCube instance to copy the values from.</param>
        protected NorthwindDataCube(NorthwindDataCube original)
        {
            OrderId = original.OrderId;
            Customer = original.Customer;
            Employee = original.Employee;
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
            Supplier = original.Supplier;
            ShipCountry = original.ShipCountry;
            Category = original.Category;
            QuantityPerUnit = original.QuantityPerUnit;
            UnitsInStock = original.UnitsInStock;
            UnitsOnOrder = original.UnitsOnOrder;
            ReorderLevel = original.ReorderLevel;
            Discontinued = original.Discontinued;
            Amount = original.Amount;
        }

        /// <summary>
        /// Gets the unique identifier for the order.
        /// </summary>
        [property: Key]
        [Dimension(typeof(Order))]
        public int OrderId { get; init; }
    
        /// <summary>
        /// Gets the identifier of the customer who placed the order.
        /// </summary>
        [property: Dimension(typeof(Customer))]
        public string Customer { get; init; }
    
        /// <summary>
        /// Gets the identifier of the employee who processed the order.
        /// </summary>
        [property: Dimension(typeof(Employee))]
        public int Employee { get; init; }
    
        /// <summary>
        /// Gets the identifier of the supplier of the product.
        /// </summary>
        [Dimension(typeof(Supplier))]
        public int Supplier { get; init; }
    
        /// <summary>
        /// Gets the identifier of the category of the product.
        /// </summary>
        [Dimension(typeof(Category))]
        public int Category { get; init; }
    
        /// <summary>
        /// Gets the shipping region for the order.
        /// </summary>
        [property: Dimension(typeof(Region))]
        public string Region { get; init; }
    
        /// <summary>
        /// Gets the date when the order was placed.
        /// </summary>
        [NotVisible]
        public DateTime OrderDate { get; init; }

        /// <summary>
        /// Gets the month when the order was placed.
        /// </summary>
        [NotVisible]
        [Dimension(typeof(string), nameof(OrderMonth))]
        public string OrderMonth { get; init; }

        /// <summary>
        /// Gets the year when the order was placed.
        /// </summary>
        [NotVisible]
        [Dimension(typeof(int), nameof(OrderYear))]
        public int OrderYear { get; init; }

        /// <summary>
        /// Gets the date by which the order is required.
        /// </summary>
        [NotVisible]
        public DateTime RequiredDate { get; init; }
    
        /// <summary>
        /// Gets the date when the order was shipped.
        /// </summary>
        [NotVisible]
        public DateTime ShippedDate { get; init; }
    
        /// <summary>
        /// Gets the identifier of the shipper used for the order.
        /// </summary>
        [NotVisible]
        public int ShipVia { get; init; }
    
        /// <summary>
        /// Gets the country to which the order was shipped.
        /// </summary>
        [NotVisible]
        public string ShipCountry { get; init; }
    
        /// <summary>
        /// Gets the freight charge for the order.
        /// </summary>
        [NotVisible]
        public decimal Freight { get; init; }

        /// <summary>
        /// Gets the identifier of the product.
        /// </summary>
        [property: Dimension(typeof(Product))]
        public int Product { get; init; }

        /// <summary>
        /// Gets the name of the product.
        /// </summary>
        [NotVisible]
        public string ProductName { get; init; }
    
        /// <summary>
        /// Calculates the total amount for the order detail, considering unit price, quantity, and discount.
        /// </summary>
        public double Amount { get; init; }
    
        /// <summary>
        /// Gets the price per unit of the product.
        /// </summary>
        [NotVisible]
        public double UnitPrice { get; init; }
    
        /// <summary>
        /// Gets the quantity of the product ordered.
        /// </summary>
        [NotVisible]
        public int Quantity { get; init; }
    
        /// <summary>
        /// Gets the discount applied to the order.
        /// </summary>
        [NotVisible]
        [Dimension(typeof(double), nameof(Discount))]
        public double Discount { get; init; }
    
        /// <summary>
        /// Gets the quantity per unit for the product.
        /// </summary>
        [NotVisible]
        public string QuantityPerUnit { get; init; }
    
        /// <summary>
        /// Gets the number of units in stock for the product.
        /// </summary>
        [NotVisible]
        public short UnitsInStock { get; init; }
    
        /// <summary>
        /// Gets the number of units on order for the product.
        /// </summary>
        [NotVisible]
        public short UnitsOnOrder { get; init; }
    
        /// <summary>
        /// Gets the reorder level for the product.
        /// </summary>
        [NotVisible]
        public short ReorderLevel { get; init; }
    
        /// <summary>
        /// Indicates whether the product is discontinued.
        /// </summary>
        [NotVisible]
        public string Discontinued { get; init; }
    }
 /// <summary>
    /// Initializes a new instance of the <see cref="LabeledNorthwindDataCube"/> class with the specified label and original data cube.
    /// </summary>
        public record LabeledNorthwindDataCube : NorthwindDataCube
    {
       /// <summary>
    /// Initializes a new instance of the <see cref="LabeledNorthwindDataCube"/> class.
    /// </summary> 
        public LabeledNorthwindDataCube(string label, NorthwindDataCube original) : base(original)
        {
            Label = label;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LabeledNorthwindDataCube"/> class.
        /// </summary>
        public LabeledNorthwindDataCube() { }
 /// <summary>
    /// Gets the label for the data cube.
    /// </summary>
        [NotVisible]
        [Dimension(typeof(string), nameof(Label))]
        public string Label { get; init; }
    }
}
