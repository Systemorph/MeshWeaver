using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OpenSmc.GridModel;
using Xunit;

namespace OpenSmc.Reporting.Test
{
    public class GridOptionsTests
    {
        [Fact]
        public void GridOptionsSerialization()
        {
            var settings = new JsonSerializerSettings
                           {
                               ContractResolver = new CamelCasePropertyNamesContractResolver(),
                               NullValueHandling = NullValueHandling.Ignore,
                               DefaultValueHandling = DefaultValueHandling.Ignore,
                           };

            var expectedResult = "{\"rowData\":[{\"a\":1,\"b\":2}],\"components\":{},\"columnHoverHighlight\":true}";

            var value = new GridOptions { RowData = new[] { new { A = 1, b = 2 } } };
            var actualResult = JsonConvert.SerializeObject(value, settings);

            actualResult.Should().Be(expectedResult);
        }
    }
}
