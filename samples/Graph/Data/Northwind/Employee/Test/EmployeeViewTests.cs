// <meshweaver>
// Id: EmployeeViewTests
// DisplayName: Employee Views Tests — the views are templates
// </meshweaver>
#nullable enable
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// The cases of <c>Northwind/Employee</c>'s views: each view is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later") — built without the node, its first emission is the whole page,
/// with no view deferred until data arrives — and it binds the node by PATH. Pure cases: they
/// build the templates from a path and THROW on failure. <see cref="Tests"/> runs them; a case not
/// listed there runs nowhere.
/// </summary>
public static class EmployeeViewTests
{
    private const string NodePath = "Northwind/Employee/Andrew_Fuller";

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame: the <c>N/M passed</c> title and a row per case.</returns>
    public static UiControl Tests(LayoutAreaHost host, RenderingContext context)
    {
        var cases = new (string Name, Action Body)[]
        {
            ("The overview is a template: the heading is the node's name, the rest its content.", Overview_IsATemplateBoundByPath),
            ("Employment is whole while its feed has said nothing yet.", Employment_IsATemplate_EvenWhileItsFeedIsSilent),
            ("The employment details are computed for the viewer: dates in their format, years of service.", EmploymentMarkdown_ComputesForTheViewer),
        };
        var rows = cases.Select(c => Run(c.Name, c.Body)).ToImmutableList();
        return Controls.Stack
            .WithView(Controls.Markdown($"### Employee views tests — {rows.Count(r => r.Passed)}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }

    /// <summary>The overview is a template: the heading is the node's name, the rest its content.</summary>
    public static void Overview_IsATemplateBoundByPath()
    {
        var template = EmployeeNodeLayoutAreas.OverviewTemplate(NodePath);
        ExpectTemplate(template, "EmployeeOverview");
        var bindings = Bindings(template);
        Expect(bindings.Any(b => b.Pointer == "name" && b.Context == LayoutAreaReference.GetMeshNodeDataContext(NodePath, bindContent: false)),
            "the heading binds the node's own name");
        foreach (var pointer in new[] { "employeeId", "firstName", "lastName", "city", "region", "country", "title", "titleOfCourtesy" })
            Expect(bindings.Any(b => b.Pointer == pointer && b.Context == LayoutAreaReference.GetMeshNodeDataContext(NodePath)),
                $"the overview binds '{pointer}' in the content");
    }

    /// <summary>Employment is whole while its feed has said nothing yet.</summary>
    public static void Employment_IsATemplate_EvenWhileItsFeedIsSilent()
    {
        var template = EmployeeNodeLayoutAreas.EmploymentTemplate(Observable.Never<string>());
        ExpectTemplate(template, "Employment");
        if (LayoutTemplate.Descendants(template).OfType<MarkdownControl>().SingleOrDefault() is not { } body)
            throw new InvalidOperationException("the details are a markdown control declared up front");
        Expect(body.DataContext == LayoutAreaReference.GetDataPointer(EmployeeNodeLayoutAreas.EmploymentDataId),
            $"the details are bound to /data/{EmployeeNodeLayoutAreas.EmploymentDataId}, got {body.DataContext}");
    }

    /// <summary>The employment details are computed for the viewer: dates in their format, years of service.</summary>
    public static void EmploymentMarkdown_ComputesForTheViewer()
    {
        var employee = new EmployeeContent { HireDate = new DateTime(1992, 8, 14), BirthDate = new DateTime(1952, 2, 19), ReportsTo = 2 };
        var english = EmployeeNodeLayoutAreas.EmploymentMarkdown(employee, CultureInfo.GetCultureInfo("en"), new DateTime(2026, 10, 1));
        Expect(english.Contains("**Years of Service:** 34", StringComparison.Ordinal), english);
        Expect(english.Contains("Employee #2", StringComparison.Ordinal), english);
        Expect(english.Contains("August 14, 1992", StringComparison.Ordinal) || english.Contains("14 August 1992", StringComparison.Ordinal), english);
        var german = EmployeeNodeLayoutAreas.EmploymentMarkdown(employee, CultureInfo.GetCultureInfo("de"), new DateTime(2026, 10, 1));
        Expect(german.Contains("August 1992", StringComparison.Ordinal) && german.Contains("14.", StringComparison.Ordinal), german);
        var blank = EmployeeNodeLayoutAreas.EmploymentMarkdown(new EmployeeContent(), CultureInfo.GetCultureInfo("en"), new DateTime(2026, 10, 1));
        Expect(blank.Contains("**Hire Date:** —", StringComparison.Ordinal) && blank.Contains("**Years of Service:** 0", StringComparison.Ordinal), blank);
    }

    /// <summary>The node-bound pointers of the tree's labels, as <c>(pointer, data context)</c>.</summary>
    private static ImmutableList<(string Pointer, string? Context)> Bindings(UiControl root) =>
        LayoutTemplate.Descendants(root)
            .OfType<LabelControl>()
            .Where(label => label.Data is JsonPointerReference)
            .Select(label => (((JsonPointerReference)label.Data).Pointer, label.DataContext))
            .ToImmutableList();

    private static void ExpectTemplate(UiControl template, string view)
    {
        var deferred = LayoutTemplate.DeferredViews(template);
        Expect(deferred.IsEmpty, $"{view} defers {deferred.Count} view(s) until data arrives: {string.Join(", ", deferred)}");
        Expect(!LayoutTemplate.Descendants(template).OfType<HtmlControl>().Any(),
            $"{view} renders hand-built HTML; compose platform controls instead");
    }

    private static EmployeeViewTestsRow Run(string name, Action body)
    {
        try
        {
            body();
            return new(name, "✅ pass");
        }
        catch (Exception ex)
        {
            return new(name, "❌ " + ex.Message.Replace('\n', ' '));
        }
    }

    private static void Expect(bool condition, string because)
    {
        if (!condition)
            throw new InvalidOperationException(because);
    }
}

/// <summary>One executed case, as a grid row.</summary>
/// <param name="Case">What the case asserts.</param>
/// <param name="Result">✅, or ❌ with the failure message.</param>
public sealed record EmployeeViewTestsRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
}
