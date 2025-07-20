using FluentAssertions;
using System.Text.Json;
using MeshWeaver.GridModel;
using System.Collections.Immutable;
using System.Linq;
using Xunit;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;

namespace MeshWeaver.Reporting.Test
{
    public class GridOptionsTests(ITestOutputHelper output) : HubTestBase(output)
    {
        protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
            => base.ConfigureClient(configuration).AddGridModel(); [Fact]
        public void GridOptionsSerialization()
        {
            var options = GetClient().JsonSerializerOptions;

            var value = new GridOptions { RowData = new[] { new { A = 1, b = 2 } } };
            var actualResult = JsonSerializer.Serialize(value, options);

            // Verify the essential parts are present - the exact format may include type information
            actualResult.Should().Contain("\"rowData\":[{");
            actualResult.Should().Contain("\"components\":{}");
            actualResult.Should().Contain("\"columnHoverHighlight\":true");

            // Verify round-trip serialization works
            var deserializedValue = JsonSerializer.Deserialize<GridOptions>(actualResult, options);
            deserializedValue.Should().NotBeNull();
            deserializedValue.ColumnHoverHighlight.Should().BeTrue();
            deserializedValue.Components.Should().NotBeNull();
        }
        [Fact]
        public void GridOptions_ColumnGroupsWithChildren_ShouldSerializeChildren()
        {
            // Arrange
            var options = GetClient().JsonSerializerOptions;

            var childColumn1 = new ColDef { Field = "firstName", HeaderName = "First Name" };
            var childColumn2 = new ColDef { Field = "lastName", HeaderName = "Last Name" };

            var columnGroup = new ColGroupDef
            {
                HeaderName = "Name Group",
                GroupId = "nameGroup",
                OpenByDefault = true,
                Children = ImmutableList.Create(childColumn1, childColumn2)
            };

            var gridOptions = new GridOptions
            {
                ColumnDefs = new[] { columnGroup },
                RowData = new[] { new { firstName = "John", lastName = "Doe" } }
            };

            // Act
            var serializedJson = JsonSerializer.Serialize(gridOptions, options);

            // Debug output to see what's actually serialized
            Output.WriteLine("Serialized JSON:");
            Output.WriteLine(serializedJson);

            // Assert - This test should pass with System.Text.Json polymorphic serialization
            serializedJson.Should().Contain("children");
            serializedJson.Should().Contain("firstName");
            serializedJson.Should().Contain("lastName");
            serializedJson.Should().Contain("nameGroup");

            // Verify we can deserialize back and get the same structure
            var deserializedOptions = JsonSerializer.Deserialize<GridOptions>(serializedJson, options)!;
            deserializedOptions.ColumnDefs.Should().HaveCount(1);

            var deserializedGroup = deserializedOptions.ColumnDefs.First() as ColGroupDef;
            deserializedGroup.Should().NotBeNull();
            deserializedGroup.Children.Should().HaveCount(2);
            deserializedGroup.Children.First().Field.Should().Be("firstName");
            deserializedGroup.Children.Last().Field.Should().Be("lastName");
        }
    }
}
