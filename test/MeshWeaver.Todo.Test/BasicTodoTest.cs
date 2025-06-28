using FluentAssertions;
using MeshWeaver.Todo;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

public class BasicTodoTest(ITestOutputHelper output) : TodoDataTestBase(output)
{
    [Fact]
    public void TodoApplication_CanBeConfigured()
    {
        // Test that the mesh can be configured with TodoApplication
        var mesh = Mesh;
        mesh.Should().NotBeNull();
    }
}
