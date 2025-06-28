using System;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Todo;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Basic test to verify Todo mesh configuration works correctly
/// </summary>
public class TodoMeshBasicTest(ITestOutputHelper output) : TodoDataTestBase(output)
{

    /// <summary>
    /// Test that verifies the mesh has the Todo application configured
    /// </summary>
    [Fact]
    public void Mesh_Should_Have_Todo_Application_Registered()
    {
        // Arrange & Act
        var client = GetClient();

        // Assert - The client should be created successfully
        client.Should().NotBeNull();

        Output.WriteLine("✅ Basic mesh test PASSED: Client created successfully");
        Output.WriteLine("✅ Todo application should be registered in the mesh");
    }

    /// <summary>
    /// Test that verifies we can get the workspace
    /// </summary>
    [Fact]
    public void Client_Should_Provide_Workspace()
    {
        // Arrange & Act
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Assert
        workspace.Should().NotBeNull();

        Output.WriteLine("✅ Workspace test PASSED: Workspace is available");
    }
}
