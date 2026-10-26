using System.Collections.Immutable;

namespace MeshWeaver.PluginTester.Extensibility;

/// <summary>
/// The builder's evaluation of one project, as an extension sees it: the evaluated properties, the
/// SDK's default globs, and the ordered item operations of the item types the extension claims. The
/// builder's own glob rules (bin/obj, dot-directories and <c>DefaultItemExcludes</c> excluded) apply,
/// so an extension finds exactly the files the SDK's default items would.
/// </summary>
public interface IProjectEvaluation
{
    /// <summary>Absolute path of the <c>.csproj</c>.</summary>
    string ProjectPath { get; }

    /// <summary>The project's directory.</summary>
    string ProjectDirectory { get; }

    /// <summary>The <c>Sdk</c> attribute, verbatim.</summary>
    string Sdk { get; }

    /// <summary>The output assembly's simple name.</summary>
    string AssemblyName { get; }

    /// <summary>The evaluated <c>RootNamespace</c>.</summary>
    string RootNamespace { get; }

    /// <summary>An evaluated property; empty when unset.</summary>
    /// <param name="name">The property name, case-insensitive as in MSBuild.</param>
    /// <returns>The value.</returns>
    string Property(string name);

    /// <summary>Whether the operator passed this <c>--accept</c> token.</summary>
    /// <param name="token">The token.</param>
    /// <returns>True when accepted.</returns>
    bool IsAccepted(string token);

    /// <summary>Every file under the project matching <paramref name="filePattern"/> (e.g. <c>*.razor</c>),
    /// with the SDK's default-item exclusions applied.</summary>
    /// <param name="filePattern">A file-name pattern.</param>
    /// <returns>Absolute paths.</returns>
    IEnumerable<string> DefaultGlob(string filePattern);

    /// <summary>Expands an <c>Include</c>/<c>Remove</c> pattern relative to the project.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>Absolute paths.</returns>
    IEnumerable<string> ExpandGlob(string pattern);

    /// <summary>The operations on the item types this extension claims, in declaration order.</summary>
    ImmutableArray<ItemOperation> ItemOperations { get; }

    /// <summary>
    /// A refusal by name, as the builder's own refusals read; the caller throws it.
    /// </summary>
    /// <param name="message">What cannot be reproduced, and how to proceed.</param>
    /// <returns>The exception to throw.</returns>
    Exception Refuse(string message);
}

/// <summary>One <c>Include</c>/<c>Remove</c> on a claimed item type.</summary>
/// <param name="ItemType">The item type, e.g. <c>Content</c>.</param>
/// <param name="Include">The expanded, split <c>Include</c> patterns.</param>
/// <param name="Remove">The expanded, split <c>Remove</c> patterns.</param>
/// <param name="DeclaredIn">The file that declared it.</param>
public sealed record ItemOperation(
    string ItemType, ImmutableArray<string> Include, ImmutableArray<string> Remove, string DeclaredIn);

/// <summary>One scan of a repository for the type names an extension's source files declare.</summary>
public interface IDeclaredTypeScan
{
    /// <summary>Records what <paramref name="path"/> declares.</summary>
    /// <param name="path">A file with one of the extension's claimed suffixes.</param>
    /// <param name="sink">Where the names go.</param>
    void Collect(string path, IDeclaredTypeSink sink);
}

/// <summary>Where a <see cref="IDeclaredTypeScan"/> records names.</summary>
public interface IDeclaredTypeSink
{
    /// <summary>A type whose fully-qualified name is known exactly.</summary>
    /// <param name="qualifiedName">Namespace-qualified name.</param>
    void Declare(string qualifiedName);

    /// <summary>
    /// A type whose namespace is only known to END with <paramref name="dottedDirectory"/> (empty:
    /// any namespace) — the shape of a generated type whose root namespace is the SDK's to compute.
    /// </summary>
    /// <param name="simpleName">The type's simple name.</param>
    /// <param name="dottedDirectory">The namespace suffix.</param>
    void DeclareBySuffix(string simpleName, string dottedDirectory);
}
