using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Northwind.ViewModel;

public record ProductOverviewItem
{
    public int ProductId { get; init; }
    
    public string ProductName { get; init; }
    
    public string CategoryName { get; init; }

    [DisplayFormat(DataFormatString = "N2")]
    public double UnitPrice { get; init; }

    public int UnitsSold { get; init; }

    [DisplayFormat(DataFormatString = "N2")]
    public double DiscountGiven { get; init; }

    [DisplayFormat(DataFormatString = "N2")]
    public double TotalAmount { get; init; }
}
