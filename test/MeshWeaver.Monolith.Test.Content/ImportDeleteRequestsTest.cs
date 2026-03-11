using System;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Monolith.Test.Content;

/// <summary>
/// Unit tests for import/delete request and response records.
/// </summary>
public class ImportDeleteRequestsTest
{
    #region ImportNodesResponse Tests

    [Fact]
    public void ImportNodesResponse_Ok_SetsProperties()
    {
        var response = ImportNodesResponse.Ok(10, 20, 3, 5, TimeSpan.FromSeconds(2.5));

        response.Success.Should().BeTrue();
        response.Error.Should().BeNull();
        response.NodesImported.Should().Be(10);
        response.PartitionsImported.Should().Be(20);
        response.NodesSkipped.Should().Be(3);
        response.PartitionsSkipped.Should().Be(5);
        response.Elapsed.TotalSeconds.Should().BeApproximately(2.5, 0.01);
    }

    [Fact]
    public void ImportNodesResponse_Fail_SetsError()
    {
        var response = ImportNodesResponse.Fail("Source not found");

        response.Success.Should().BeFalse();
        response.Error.Should().Be("Source not found");
        response.NodesImported.Should().Be(0);
        response.PartitionsImported.Should().Be(0);
    }

    [Fact]
    public void ImportNodesResponse_Ok_ZeroCounts_IsStillSuccess()
    {
        var response = ImportNodesResponse.Ok(0, 0, 0, 0, TimeSpan.Zero);

        response.Success.Should().BeTrue();
        response.NodesImported.Should().Be(0);
    }

    #endregion

    #region ImportContentResponse Tests

    [Fact]
    public void ImportContentResponse_Ok_SetsFilesImported()
    {
        var response = ImportContentResponse.Ok(42);

        response.Success.Should().BeTrue();
        response.Error.Should().BeNull();
        response.FilesImported.Should().Be(42);
    }

    [Fact]
    public void ImportContentResponse_Fail_SetsError()
    {
        var response = ImportContentResponse.Fail("Collection not found");

        response.Success.Should().BeFalse();
        response.Error.Should().Be("Collection not found");
        response.FilesImported.Should().Be(0);
    }

    #endregion

    #region DeleteContentResponse Tests

    [Fact]
    public void DeleteContentResponse_Ok_SetsItemsDeleted()
    {
        var response = DeleteContentResponse.Ok(15);

        response.Success.Should().BeTrue();
        response.Error.Should().BeNull();
        response.ItemsDeleted.Should().Be(15);
    }

    [Fact]
    public void DeleteContentResponse_Fail_SetsError()
    {
        var response = DeleteContentResponse.Fail("Permission denied");

        response.Success.Should().BeFalse();
        response.Error.Should().Be("Permission denied");
        response.ItemsDeleted.Should().Be(0);
    }

    #endregion

    #region Request Record Tests

    [Fact]
    public void ImportNodesRequest_StoresProperties()
    {
        var request = new ImportNodesRequest("/data/seed", "target/path") { Force = true };

        request.SourcePath.Should().Be("/data/seed");
        request.TargetPath.Should().Be("target/path");
        request.Force.Should().BeTrue();
    }

    [Fact]
    public void ImportNodesRequest_Force_DefaultsFalse()
    {
        var request = new ImportNodesRequest("/data/seed", "target/path");

        request.Force.Should().BeFalse();
    }

    [Fact]
    public void ImportContentRequest_StoresProperties()
    {
        var request = new ImportContentRequest("attachments", "/data/files", "org/acme");

        request.CollectionName.Should().Be("attachments");
        request.SourcePath.Should().Be("/data/files");
        request.TargetPath.Should().Be("org/acme");
    }

    [Fact]
    public void DeleteContentRequest_StoresProperties()
    {
        var request = new DeleteContentRequest("attachments", "org/acme");

        request.CollectionName.Should().Be("attachments");
        request.FolderPath.Should().Be("org/acme");
    }

    #endregion
}
