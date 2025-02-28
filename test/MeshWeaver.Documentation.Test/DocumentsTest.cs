using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The main class for testing documentation
/// </summary>
/// <param name="output"></param>
public class DocumentsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configure the documentation service
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) => 
        configuration.ConfigureDocumentationTestHost();

    /// <summary>
    /// This is how to retrieve a file from documentation service
    /// </summary>
    [HubFact]
    public async Task TestRetrievingFile()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var stream = documentationService.GetStream(EmbeddedDocumentationSource.Embedded, "MeshWeaver.Documentation.Test", "Readme.md");
        stream.Should().NotBeNull();
        var content = await new StreamReader(stream).ReadToEndAsync();
        content.Should().NotBeNullOrEmpty();
    }


    /// <summary>
    /// Here we read the source from embedded assemblies
    /// </summary>
    /// <returns></returns>

    [HubFact]
    public async Task TryReadSource()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var type = GetType();
        var sourceByType = (PdbDocumentationSource)documentationService.GetSource(PdbDocumentationSource.Pdb, type.Assembly.GetName().Name);
        sourceByType.Should().NotBeNull();
        var fileName = sourceByType.FilesByType.GetValueOrDefault(typeof(DocumentsTest).FullName);
        fileName.Should().Be($"{nameof(DocumentsTest)}.cs");
        await using var stream = sourceByType.GetStream(fileName);
        stream.Should().NotBeNull();
        var content = await new StreamReader(stream).ReadToEndAsync();
        content.Should().NotBeNullOrWhiteSpace();
    }


    /// <summary>
    /// This tests reading debug info from the pdb
    /// </summary>
    [HubFact]
    public void TestDebugInfo()
    {
        var points = PdbDocumentationSource.ReadMethodSourceInfo(typeof(DocumentsTest).Assembly.Location, nameof(TestDebugInfo));
        points.Should().NotBeNull();
        points.Should().HaveCountGreaterThan(0);
    }

}
