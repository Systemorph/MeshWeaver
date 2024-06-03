using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Northwind.Domain
{
    public static class NorthwindDomain
    {
        public static Type[] OperationalTypes { get; } =
            [typeof(Order), typeof(OrderDetails), typeof(Supplier), typeof(Employee), typeof(Product), typeof(Customer)];
        public static Type[] ReferenceDataTypes { get; } = [typeof(Category), typeof(Region), typeof(Territory)];

    }
}
