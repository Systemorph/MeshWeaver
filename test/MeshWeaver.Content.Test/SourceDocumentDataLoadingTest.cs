using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Content.Test;

/// <summary>
/// Generic integration tests for verifying that node types correctly load data
/// from source documents (JSON files, Markdown files, etc.).
///
/// These tests verify the data loading infrastructure works correctly for any
/// node type that has source documents configured, not just specific use cases.
/// </summary>
[Collection("SourceDocumentDataLoadingTests")]
public class SourceDocumentDataLoadingTest : MonolithMeshTestBase
{
    public SourceDocumentDataLoadingTest(ITestOutputHelper output) : base(output)
    {
    }

    // Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData))
            .AddCornerstone()
            .AddGraph();

    /// <summary>
    /// Test case definition for source document data loading tests.
    /// </summary>
    public record DataLoadingTestCase(
        string NodeAddress,
        string TypeName,
        int? ExpectedMinCount = null,
        string? ExpectedIdPattern = null,
        string Description = "")
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Description)
                ? $"{NodeAddress} -> {TypeName}"
                : Description;
    }

    /// <summary>
    /// Test cases for Cornerstone pricing nodes.
    /// Add new test cases here when creating new use cases with source documents.
    /// </summary>
    public static IEnumerable<object[]> CornerstonePricingTestCases =>
        new List<object[]>
        {
            // Microsoft 2026 - PropertyRisks from JSON
            new object[] { new DataLoadingTestCase(
                "Cornerstone/Microsoft/2026",
                "PropertyRisk",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load PropertyRisks from JSON") },

            // Microsoft 2026 - ReinsuranceAcceptances from Slip.md
            new object[] { new DataLoadingTestCase(
                "Cornerstone/Microsoft/2026",
                "ReinsuranceAcceptance",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load ReinsuranceAcceptances from Slip.md") },

            // Microsoft 2026 - ReinsuranceSections from Slip.md
            new object[] { new DataLoadingTestCase(
                "Cornerstone/Microsoft/2026",
                "ReinsuranceSection",
                ExpectedMinCount: 1,
                Description: "Microsoft 2026 should load ReinsuranceSections from Slip.md") },
        };

    /// <summary>
    /// Verifies that a node type correctly loads data from its configured source documents.
    /// This is a parameterized test that can be used for any node type.
    /// </summary>
    /// <remarks>
    /// Method timeout must exceed the inner data-load wait below. The Ping + cold-start
    /// NodeType compilation + source-document load consume real time before the
    /// <c>.Within(10.Seconds())</c> data wait can even begin observing; a 10 s method
    /// timeout left zero headroom and aborted the first (cold) test rows mid-load.
    /// 30 s gives the activation cost room ahead of the bounded data wait.
    /// </remarks>
    [Theory(Timeout = 30000)]
    [MemberData(nameof(CornerstonePricingTestCases))]
    public async Task NodeType_LoadsDataFromSourceDocuments(DataLoadingTestCase testCase)
    {
        var client = GetClient();
        var addressParts = testCase.NodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();

        // Get the workspace
        var hub = Mesh.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Get data for the specified type. Cold-start NodeType compilation + source-document
        // load can exceed 10 s when this test runs against a freshly-disposed mesh (no warm
        // type registry), so the inner reactive wait gets a 25 s budget inside the 30 s method
        // timeout — the load completes, it is just slow on the cold path.
        var data = (await GetDynamicObservable(workspace, testCase.TypeName)
            .Should()
            .Within(25.Seconds())
            .Match(d => d != null && d.Length > 0))!;

        // Verify data was loaded
        data.Should().NotBeNull($"{testCase.TypeName} should be loaded for {testCase.NodeAddress}");
        data.Should().NotBeEmpty($"{testCase.TypeName} should have data loaded from source documents");

        // Verify minimum count if specified
        if (testCase.ExpectedMinCount.HasValue)
        {
            data.Should().HaveCountGreaterThanOrEqualTo(
                testCase.ExpectedMinCount.Value,
                $"{testCase.TypeName} should have at least {testCase.ExpectedMinCount} records");
        }

        // Verify ID pattern if specified
        if (!string.IsNullOrEmpty(testCase.ExpectedIdPattern))
        {
            var ids = data.Select(d => GetProperty<string>(d, "Id")).Where(id => id != null).ToList();
            ids.Should().Contain(id => id!.Contains(testCase.ExpectedIdPattern),
                $"{testCase.TypeName} should have records with IDs matching '{testCase.ExpectedIdPattern}'");
        }
    }

    /// <summary>
    /// Verifies that a node hub can be initialized and has the expected data types registered.
    /// </summary>
    /// <remarks>
    /// The method loops over three types, each polling the registry for up to 8 s, so the
    /// cumulative inner wait can reach ~24 s when NodeType compilation is cold. A 10 s method
    /// timeout aborted the loop mid-compile; 40 s covers the cold compile that the first
    /// type's wait absorbs (the remaining types are already warm).
    /// </remarks>
    [Theory(Timeout = 40000)]
    [InlineData("Cornerstone/Microsoft/2026", new[] { "PropertyRisk", "ReinsuranceAcceptance", "ReinsuranceSection" })]
    public async Task NodeHub_HasExpectedDataTypesRegistered(string nodeAddress, string[] expectedTypes)
    {
        var client = GetClient();
        var addressParts = nodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();

        // Get the type registry
        var hub = Mesh.GetHostedHub(address);
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Verify all expected types are registered. The ping only confirms the hub
        // exists; NodeType compilation + type registration happens asynchronously
        // after activation, so poll the registry until each type appears.
        foreach (var typeName in expectedTypes)
        {
            var type = await Observable.Interval(TimeSpan.FromMilliseconds(50))
                .StartWith(0L)
                .Select(_ => typeRegistry.GetType(typeName))
                .Where(t => t != null)
                .Should()
                .Within(8.Seconds())
                .Emit();
            type.Should().NotBeNull($"Type '{typeName}' should be registered for {nodeAddress}");
        }
    }

    /// <summary>
    /// Verifies that source document files exist for a node type.
    /// This helps catch configuration issues early.
    /// </summary>
    [Theory(Timeout = 5000)]
    [InlineData("Cornerstone/Microsoft/2026", "Submissions/Slip.md")]
    public void SourceDocuments_ExistInFileSystem(string nodeAddress, string relativePath)
    {
        var basePath = Path.Combine(TestPaths.SamplesGraph, "attachments");
        var addressParts = nodeAddress.Split('/');

        // Build the full path: basePath/Cornerstone/Microsoft/2026/Submissions/file
        var fullPath = Path.Combine(basePath, Path.Combine(addressParts), relativePath);

        File.Exists(fullPath).Should().BeTrue(
            $"Source document should exist at {fullPath}");
    }

    /// <summary>
    /// Verifies that data loaded from source documents has valid structure.
    /// Checks that key properties are present and have valid values.
    /// </summary>
    /// <remarks>
    /// Same timeout headroom rationale as <see cref="NodeType_LoadsDataFromSourceDocuments"/>:
    /// the inner <c>.Within(10.Seconds())</c> data wait needs room ahead of it for the
    /// Ping + cold-start activation, so the method timeout is 30 s, not 10 s.
    /// </remarks>
    [Theory(Timeout = 30000)]
    [InlineData("Cornerstone/Microsoft/2026", "PropertyRisk", "Id", "LocationName")]
    [InlineData("Cornerstone/Microsoft/2026", "ReinsuranceAcceptance", "Id", "Name")]
    [InlineData("Cornerstone/Microsoft/2026", "ReinsuranceSection", "Id", "Limit")]
    public async Task LoadedData_HasValidStructure(string nodeAddress, string typeName, params string[] requiredProperties)
    {
        var client = GetClient();
        var addressParts = nodeAddress.Split('/');
        var address = new Address(addressParts);

        // Initialize the node hub
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();

        // Get the workspace and data
        var hub = Mesh.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Cold-start headroom — see NodeType_LoadsDataFromSourceDocuments. The inner wait
        // gets 25 s inside the 30 s method timeout so a cold NodeType compile + source load
        // can finish before the assertion gives up.
        var data = (await GetDynamicObservable(workspace, typeName)
            .Should()
            .Within(25.Seconds())
            .Match(d => d != null && d.Length > 0))!;

        // Verify each record has the required properties with non-null values
        foreach (var record in data)
        {
            var recordType = record.GetType();

            foreach (var propName in requiredProperties)
            {
                var prop = recordType.GetProperty(propName);
                prop.Should().NotBeNull($"{typeName} should have property '{propName}'");

                var value = prop!.GetValue(record);
                value.Should().NotBeNull($"{typeName}.{propName} should have a value");
            }
        }
    }

    #region Helper Methods

    /// <summary>
    /// Helper to get observable stream for a dynamically compiled type by name.
    /// The type is registered asynchronously after hub activation (NodeType compile),
    /// so the registry lookup is deferred + retried on subscription rather than read
    /// eagerly — the caller's <c>.Within(...)</c> window covers registration.
    /// </summary>
    private IObservable<object[]?> GetDynamicObservable(IWorkspace workspace, string typeName)
    {
        var typeRegistry = workspace.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Poll the registry until the dynamically-compiled type appears, then switch
        // to its data stream. Subscription-time evaluation means the registration race
        // is folded into the reactive wait instead of throwing synchronously.
        return Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => typeRegistry.GetType(typeName))
            .Where(type => type != null)
            .Take(1)
            .SelectMany(type => BuildTypedObservable(workspace, type!));
    }

    private static IObservable<object[]?> BuildTypedObservable(IWorkspace workspace, Type type)
    {
        // Use reflection to call GetObservable<T>(IWorkspace)
        var getObservableMethods = typeof(WorkspaceExtensions)
            .GetMethods()
            .Where(m => m.Name == nameof(WorkspaceExtensions.GetObservable) && m.IsGenericMethod);

        var getStreamMethod = getObservableMethods
            .First(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(IWorkspace))
            .MakeGenericMethod(type);

        var observable = getStreamMethod.Invoke(null, [workspace]);
        if (observable == null)
            return Observable.Return<object[]?>(null);

        // Convert IObservable<IReadOnlyCollection<T>> to IObservable<object[]?>
        var readOnlyCollectionType = typeof(IReadOnlyCollection<>).MakeGenericType(type);
        var selectMethod = typeof(Observable).GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2 && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(readOnlyCollectionType, typeof(object[]));

        Func<object?, object[]?> selector = items =>
            items == null ? null : ((IEnumerable)items).Cast<object>().ToArray();

        return (IObservable<object[]?>)selectMethod.Invoke(null, [observable, selector])!;
    }

    /// <summary>
    /// Helper to get property value from a dynamic object.
    /// </summary>
    private static T? GetProperty<T>(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);
        if (prop == null) return default;
        var value = prop.GetValue(obj);
        if (value == null) return default;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    #endregion
}

/// <summary>
/// Collection definition for SourceDocumentDataLoadingTests.
/// Ensures tests in this collection run serially to avoid resource contention.
/// </summary>
[CollectionDefinition("SourceDocumentDataLoadingTests", DisableParallelization = true)]
public class SourceDocumentDataLoadingTestsCollection
{
}
