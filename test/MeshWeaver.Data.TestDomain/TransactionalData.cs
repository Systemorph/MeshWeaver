using MeshWeaver.Messaging;
using System.ComponentModel.DataAnnotations;
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

public record ImportAddress(int Year);
public record ReferenceDataAddress();
public record ComputedDataAddress(int Year, string BusinessUnit);
public record TransactionalDataAddress(int Year, string BusinessUnit) ;
