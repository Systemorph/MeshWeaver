using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Shared test domain for the property-cell sizing tests (issue #195):
/// plain free-text strings span the full grid row, structured values keep compact cells.
/// </summary>
public static class PropertyCellSizingDomain
{
    /// <summary>Status enum used to verify enum properties keep the compact cell.</summary>
    public enum SizingStatus
    {
        /// <summary>Open state.</summary>
        Open,
        /// <summary>Closed state.</summary>
        Closed
    }

    /// <summary>Dimension type referenced by <see cref="SizingEntity.DimensionRef"/>.</summary>
    public record SizingDimension
    {
        /// <summary>The dimension key.</summary>
        [Key]
        public string SystemName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Entity covering the property-kind discrimination: free-text strings vs structured values
    /// (numbers, dates, bools, enums, dimension / mesh-node references, fixed option lists).
    /// </summary>
    public record SizingEntity
    {
        /// <summary>Plain string without structure attributes — free text, spans the full row.</summary>
        [Key]
        public string Id { get; init; } = string.Empty;

        /// <summary>Plain free-text string — spans the full row.</summary>
        [Display(Name = "Notes")]
        public string Notes { get; init; } = string.Empty;

        /// <summary>Number — keeps the compact cell.</summary>
        [Display(Name = "Count")]
        public int Count { get; init; }

        /// <summary>Enum — keeps the compact cell.</summary>
        [Display(Name = "Status")]
        public SizingStatus Status { get; init; }

        /// <summary>Date — keeps the compact cell.</summary>
        [Display(Name = "Due Date")]
        public DateTime? DueDate { get; init; }

        /// <summary>Boolean — keeps the compact cell.</summary>
        [Display(Name = "Is Active")]
        public bool IsActive { get; init; }

        /// <summary>Dimension reference (short label) — keeps the compact cell.</summary>
        [Dimension(typeof(SizingDimension))]
        [Display(Name = "Dimension")]
        public string? DimensionRef { get; init; }

        /// <summary>Mesh-node reference (stores a node path, rendered as a short label) — keeps the compact cell.</summary>
        [MeshNode("nodeType:Story")]
        [Display(Name = "Node")]
        public string? NodeRef { get; init; }

        /// <summary>String backed by fixed options (select, short label) — keeps the compact cell.</summary>
        [UiControl(Options = new[] { "Small", "Medium", "Large" })]
        [Display(Name = "Size")]
        public string? Size { get; init; }
    }

    /// <summary>Resolves a property of <see cref="SizingEntity"/> by name.</summary>
    /// <param name="name">The property name.</param>
    public static PropertyInfo Property(string name) => typeof(SizingEntity).GetProperty(name)!;
}

/// <summary>
/// Unit tests for <see cref="EditLayoutArea.WithPropertyCellSizing"/> /
/// <see cref="EditLayoutArea.IsFreeTextProperty"/>: the sizing decision that lets long
/// free-text values span the full row while structured values keep the compact
/// Xs(12).Md(6).Lg(4) cells (issue #195).
/// </summary>
public class PropertyCellSizingTest
{
    /// <summary>
    /// Plain free-text string properties (no [Dimension], no [MeshNode], no fixed Options)
    /// span the full row: Xs(12) with no Md/Lg overrides (FluentGridItem cascades the last
    /// defined smaller breakpoint upward, so Xs(12) alone means full width at every breakpoint).
    /// </summary>
    [Theory]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.Id))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.Notes))]
    public void FreeTextString_SpansFullRow(string propertyName)
    {
        var property = PropertyCellSizingDomain.Property(propertyName);

        EditLayoutArea.IsFreeTextProperty(property).Should().BeTrue();

        var skin = new LayoutGridItemSkin().WithPropertyCellSizing(property);
        skin.Xs.Should().Be(12);
        skin.Md.Should().BeNull("free-text values must keep the full row on medium screens");
        skin.Lg.Should().BeNull("free-text values must keep the full row on large screens");
    }

    /// <summary>
    /// Structured values — numbers, dates, booleans, enums, dimension / mesh-node references,
    /// and option-backed strings — keep the compact responsive cells (Xs(12).Md(6).Lg(4)).
    /// </summary>
    [Theory]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.Count))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.Status))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.DueDate))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.IsActive))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.DimensionRef))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.NodeRef))]
    [InlineData(nameof(PropertyCellSizingDomain.SizingEntity.Size))]
    public void StructuredValue_KeepsCompactCell(string propertyName)
    {
        var property = PropertyCellSizingDomain.Property(propertyName);

        EditLayoutArea.IsFreeTextProperty(property).Should().BeFalse();

        var skin = new LayoutGridItemSkin().WithPropertyCellSizing(property);
        skin.Xs.Should().Be(12);
        skin.Md.Should().Be(6);
        skin.Lg.Should().Be(4);
    }
}

/// <summary>
/// Integration test: BuildPropertyForm applies the per-property cell sizing to the rendered
/// layout grid — the grid item skins that reach the client carry Xs(12) full-width for
/// free-text strings and Xs(12).Md(6).Lg(4) for structured values (issue #195).
/// </summary>
[Collection("PropertyCellSizingRenderTests")]
public class PropertyFormCellSizingRenderTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string SizingFormView = nameof(SizingFormView);

    private UiControl SizingFormViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "sizing_entity";
        host.UpdateData(dataId, new PropertyCellSizingDomain.SizingEntity
        {
            Id = "sizing-1",
            Notes = "A long extracted value that must be readable without entering edit mode.",
            Count = 42,
            Status = PropertyCellSizingDomain.SizingStatus.Open,
            DueDate = new DateTime(2026, 1, 1),
            IsActive = true
        });

        return EditLayoutArea.BuildPropertyForm(host, typeof(PropertyCellSizingDomain.SizingEntity), dataId, canEdit: true);
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(SizingFormView, SizingFormViewDefinition));
    }

    /// <summary>
    /// Renders the property form and asserts the grid item skin of every property cell:
    /// the free-text string cells span the full row, the structured cells stay compact.
    /// </summary>
    [Fact]
    public async Task PropertyForm_SizesCellsByPropertyKind()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(SizingFormView));

        var control = await area
            .GetControlStream(SizingFormView)
            .Should().Within(10.Seconds()).Match(x => x is StackControl);
        var formStack = (StackControl)control!;

        // BuildPropertyForm puts the regular properties into a LayoutGrid as the first area.
        var gridAreaId = formStack.Areas.First().Area.ToString()!;
        var gridControl = await area
            .GetControlStream(gridAreaId)
            .Should().Within(10.Seconds()).Match(x => x is LayoutGridControl);
        var grid = (LayoutGridControl)gridControl!;

        // Grid areas follow the declaration order of the (browsable, non-title) properties.
        var expectedProperties = typeof(PropertyCellSizingDomain.SizingEntity).GetProperties()
            .Where(p => !EditLayoutArea.IsTitleProperty(p.Name))
            .ToList();
        grid.Areas.Should().HaveCount(expectedProperties.Count);

        for (var i = 0; i < expectedProperties.Count; i++)
        {
            var property = expectedProperties[i];
            var skin = grid.Areas[i].Skins.OfType<LayoutGridItemSkin>().Single();

            skin.Xs.Should().Be(12, $"every property cell spans the full row on XS ({property.Name})");

            if (EditLayoutArea.IsFreeTextProperty(property))
            {
                skin.Md.Should().BeNull($"free-text property {property.Name} must span the full row on MD");
                skin.Lg.Should().BeNull($"free-text property {property.Name} must span the full row on LG");
            }
            else
            {
                skin.Md.Should().Be(6, $"structured property {property.Name} keeps the compact MD cell");
                skin.Lg.Should().Be(4, $"structured property {property.Name} keeps the compact LG cell");
            }
        }
    }
}
