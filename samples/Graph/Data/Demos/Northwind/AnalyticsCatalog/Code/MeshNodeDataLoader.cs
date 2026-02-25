// <meshweaver>
// Id: MeshNodeDataLoader
// DisplayName: MeshNode Data Loader
// </meshweaver>

using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Loads master data from MeshNode instances instead of CSV files.
/// Queries Product, Customer, Employee, and Supplier MeshNodes and converts them to record types.
/// </summary>
public static class MeshNodeDataLoader
{
    public static IObservable<IEnumerable<Product>> LoadProductsFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
        var namespacePath = GetNamespacePath(workspace);

        return meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:Demos/Northwind/Product path:{namespacePath}/Product scope:children state:Active"))
            .Select(change => change.Items.Select(node => ConvertToProduct(node)).Where(p => p != null).Cast<Product>());
    }

    public static IObservable<IEnumerable<Customer>> LoadCustomersFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
        var namespacePath = GetNamespacePath(workspace);

        return meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:Demos/Northwind/Customer path:{namespacePath}/Customer scope:children state:Active"))
            .Select(change => change.Items.Select(node => ConvertToCustomer(node)).Where(c => c != null).Cast<Customer>());
    }

    public static IObservable<IEnumerable<Employee>> LoadEmployeesFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
        var namespacePath = GetNamespacePath(workspace);

        return meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:Demos/Northwind/Employee path:{namespacePath}/Employee scope:children state:Active"))
            .Select(change => change.Items.Select(node => ConvertToEmployee(node)).Where(e => e != null).Cast<Employee>());
    }

    public static IObservable<IEnumerable<Supplier>> LoadSuppliersFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
        var namespacePath = GetNamespacePath(workspace);

        return meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:Demos/Northwind/Supplier path:{namespacePath}/Supplier scope:children state:Active"))
            .Select(change => change.Items.Select(node => ConvertToSupplier(node)).Where(s => s != null).Cast<Supplier>());
    }

    private static string GetNamespacePath(IWorkspace workspace)
    {
        var address = workspace.Hub.Address;
        // Get the namespace path (everything except the last segment which is AnalyticsCatalog)
        return string.Join("/", address.Segments.Take(address.Segments.Length - 1));
    }

    private static Product? ConvertToProduct(MeshNode node)
    {
        if (node.Content is not JsonElement json) return null;

        return new Product
        {
            ProductId = GetInt(json, "productId"),
            ProductName = GetString(json, "productName"),
            SupplierId = GetInt(json, "supplierId"),
            CategoryId = GetInt(json, "categoryId"),
            QuantityPerUnit = GetString(json, "quantityPerUnit"),
            UnitPrice = GetDouble(json, "unitPrice"),
            UnitsInStock = GetShort(json, "unitsInStock"),
            UnitsOnOrder = GetShort(json, "unitsOnOrder"),
            ReorderLevel = GetShort(json, "reorderLevel"),
            Discontinued = GetBool(json, "discontinued") ? "1" : "0"
        };
    }

    private static Customer? ConvertToCustomer(MeshNode node)
    {
        if (node.Content is not JsonElement json) return null;

        return new Customer
        {
            CustomerId = GetString(json, "customerId"),
            CompanyName = GetString(json, "companyName"),
            ContactName = GetString(json, "contactName"),
            ContactTitle = GetString(json, "contactTitle"),
            City = GetString(json, "city"),
            Region = GetString(json, "region"),
            PostalCode = GetString(json, "postalCode"),
            Country = GetString(json, "country"),
            Phone = GetString(json, "phone"),
            Fax = GetString(json, "fax")
        };
    }

    private static Employee? ConvertToEmployee(MeshNode node)
    {
        if (node.Content is not JsonElement json) return null;

        return new Employee
        {
            EmployeeId = GetInt(json, "employeeId"),
            LastName = GetString(json, "lastName"),
            FirstName = GetString(json, "firstName"),
            Title = GetString(json, "title"),
            TitleOfCourtesy = GetString(json, "titleOfCourtesy"),
            BirthDate = GetDateTime(json, "birthDate"),
            HireDate = GetDateTime(json, "hireDate"),
            City = GetString(json, "city"),
            Region = GetString(json, "region"),
            Country = GetString(json, "country"),
            ReportsTo = GetInt(json, "reportsTo")
        };
    }

    private static Supplier? ConvertToSupplier(MeshNode node)
    {
        if (node.Content is not JsonElement json) return null;

        return new Supplier
        {
            SupplierId = GetInt(json, "supplierId"),
            CompanyName = GetString(json, "companyName"),
            ContactName = GetString(json, "contactName"),
            ContactTitle = GetString(json, "contactTitle"),
            City = GetString(json, "city"),
            Region = GetString(json, "region"),
            Country = GetString(json, "country"),
            Phone = GetString(json, "phone")
        };
    }

    private static string GetString(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (json.TryGetProperty(pascal, out var prop2) && prop2.ValueKind == JsonValueKind.String)
            return prop2.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (json.TryGetProperty(pascal, out var prop2) && prop2.ValueKind == JsonValueKind.Number)
            return prop2.GetInt32();
        return 0;
    }

    private static double GetDouble(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();
        var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (json.TryGetProperty(pascal, out var prop2) && prop2.ValueKind == JsonValueKind.Number)
            return prop2.GetDouble();
        return 0;
    }

    private static short GetShort(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt16();
        var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (json.TryGetProperty(pascal, out var prop2) && prop2.ValueKind == JsonValueKind.Number)
            return prop2.GetInt16();
        return 0;
    }

    private static bool GetBool(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        var pascal = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (json.TryGetProperty(pascal, out var prop2))
        {
            if (prop2.ValueKind == JsonValueKind.True) return true;
            if (prop2.ValueKind == JsonValueKind.False) return false;
        }
        return false;
    }

    private static DateTime GetDateTime(JsonElement json, string name)
    {
        var str = GetString(json, name);
        if (!string.IsNullOrEmpty(str) && DateTime.TryParse(str, out var dt))
            return dt;
        return DateTime.MinValue;
    }
}
