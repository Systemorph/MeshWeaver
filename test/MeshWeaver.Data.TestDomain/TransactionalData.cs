using System.ComponentModel.DataAnnotations;
using MeshWeaver.Messaging;
using Newtonsoft.Json;

namespace MeshWeaver.Data.TestDomain;

/// <summary>
/// This is structuring element for sub-dividing a data domain into several groups.
/// You can perceive this as the building plan for how everyone starts.
/// For tests, it is handy to ship initial values. Can be also hosted in separate file.
/// </summary>

public record TransactionalData([property: Key][JsonProperty("$id")] string Id, int Year, string LoB, string BusinessUnit, double Value);
public record TransactionalData2(string Id, int Year, string LoB, string BusinessUnit, double Value);



public record ComputedData([property: Key] string Id, int Year, string LoB, string BusinessUnit, double Value);

public record LineOfBusiness([property: Key] string SystemName, string DisplayName);
public record BusinessUnit([property: Key] string SystemName, string DisplayName);

public static class ImportAddress
{
    public const string TypeName = nameof(ImportAddress);
    public static Address Create(int year) => new(TypeName, year.ToString());
}

public static class ReferenceDataAddress
{
    public const string TypeName = nameof(ReferenceDataAddress);
    public static Address Create() => new(TypeName, "1");
}

public static class ComputedDataAddress
{
    public const string TypeName = nameof(ComputedDataAddress);
    public static Address Create(int year, string businessUnit) => new(TypeName, $"{year}-{businessUnit}");
}

public static class TransactionalDataAddress
{
    public const string TypeName = nameof(TransactionalData);
    public static Address Create(int year, string businessUnit) => new(TypeName, $"{year}-{businessUnit}");
}
