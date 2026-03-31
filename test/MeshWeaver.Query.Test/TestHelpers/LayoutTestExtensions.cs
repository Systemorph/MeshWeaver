using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Layout;

namespace MeshWeaver.Query.Test.TestHelpers;

/// <summary>
/// Extension methods for deeply inspecting UI control hierarchies in tests.
/// </summary>
public static class LayoutTestExtensions
{
    /// <summary>
    /// Recursively finds all controls of a specific type within a control hierarchy.
    /// </summary>
    /// <typeparam name="T">The type of controls to find.</typeparam>
    /// <param name="root">The root control to search from.</param>
    /// <returns>An enumerable of all matching controls.</returns>
    public static IEnumerable<T> FindControls<T>(this UiControl root) where T : UiControl
    {
        if (root == null)
            yield break;

        if (root is T match)
            yield return match;

        // Check if this is a container control with Areas
        if (root is IContainerControl container)
        {
            // Note: Areas contain NamedAreaControl references, not the actual rendered controls.
            // For deeper inspection, we'd need access to the rendered store.
            // For now, we can check the Areas themselves.
            foreach (var area in container.Areas)
            {
                if (area is T areaMatch)
                    yield return areaMatch;
            }
        }

        // Use reflection to find any Views property that might contain nested controls
        var viewsProperty = root.GetType().GetProperty("Views", BindingFlags.NonPublic | BindingFlags.Instance);
        if (viewsProperty?.GetValue(root) is IEnumerable<object> views)
        {
            foreach (var view in views)
            {
                if (view is UiControl control)
                {
                    foreach (var nested in control.FindControls<T>())
                        yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Finds a control by its associated property name (checks JsonPointerReference).
    /// </summary>
    /// <param name="root">The root control to search from.</param>
    /// <param name="propertyName">The property name to search for (case-insensitive).</param>
    /// <returns>The matching control, or null if not found.</returns>
    public static UiControl? FindControlForProperty(this UiControl root, string propertyName)
    {
        if (root == null)
            return null;

        // Check if this control has a Data property with JsonPointerReference
        var dataProperty = root.GetType().GetProperty("Data");
        if (dataProperty?.GetValue(root) is JsonPointerReference reference)
        {
            if (reference.Pointer.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                return root;
        }

        // Check Value property for controls like MarkdownEditorControl
        var valueProperty = root.GetType().GetProperty("Value");
        if (valueProperty?.GetValue(root) is JsonPointerReference valueRef)
        {
            if (valueRef.Pointer.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                return root;
        }

        // Check container areas
        if (root is IContainerControl container)
        {
            foreach (var area in container.Areas)
            {
                var found = FindControlForProperty(area, propertyName);
                if (found != null)
                    return found;
            }
        }

        // Search in Views (for EditorControl and similar containers)
        var viewsProperty = root.GetType().GetProperty("Views", BindingFlags.NonPublic | BindingFlags.Instance);
        if (viewsProperty?.GetValue(root) is IEnumerable<object> views)
        {
            foreach (var view in views)
            {
                if (view is UiControl control)
                {
                    var found = FindControlForProperty(control, propertyName);
                    if (found != null)
                        return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Asserts that the control hierarchy contains a property editor of the expected type.
    /// </summary>
    /// <param name="root">The root control to search from.</param>
    /// <param name="propertyName">The property name to verify.</param>
    /// <param name="expectedControlType">The expected control type for this property.</param>
    public static void AssertHasPropertyEditor(this UiControl root, string propertyName, Type expectedControlType)
    {
        var control = root.FindControlForProperty(propertyName);
        control.Should().NotBeNull($"Control for property '{propertyName}' should exist");
        control.Should().BeOfType(expectedControlType, $"Property '{propertyName}' should use {expectedControlType.Name}");
    }

    /// <summary>
    /// Finds a button control by its title/data text.
    /// </summary>
    /// <param name="root">The root control to search from.</param>
    /// <param name="buttonText">The button text to search for.</param>
    /// <returns>The matching ButtonControl, or null if not found.</returns>
    public static ButtonControl? FindButton(this UiControl root, string buttonText)
    {
        return root.FindControls<ButtonControl>()
            .FirstOrDefault(b => b.Data?.ToString() == buttonText);
    }

    /// <summary>
    /// Gets the number of areas in a container control.
    /// </summary>
    /// <param name="control">The control to check.</param>
    /// <returns>The number of areas, or 0 if not a container.</returns>
    public static int GetAreaCount(this UiControl control)
    {
        if (control is IContainerControl container)
            return container.Areas.Count;
        return 0;
    }

    /// <summary>
    /// Checks if a control has Required property set to true.
    /// </summary>
    /// <param name="control">The control to check.</param>
    /// <returns>True if the control is marked as required.</returns>
    public static bool IsRequired(this UiControl control)
    {
        var requiredProperty = control.GetType().GetProperty("Required");
        if (requiredProperty?.GetValue(control) is bool required)
            return required;
        if (requiredProperty?.GetValue(control) is object obj)
            return obj?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        return false;
    }

    /// <summary>
    /// Gets the placeholder text from a control.
    /// </summary>
    /// <param name="control">The control to check.</param>
    /// <returns>The placeholder text, or null if not set.</returns>
    public static string? GetPlaceholder(this UiControl control)
    {
        var placeholderProperty = control.GetType().GetProperty("Placeholder");
        return placeholderProperty?.GetValue(control)?.ToString();
    }

    /// <summary>
    /// Verifies that an EditorControl contains the expected set of property fields.
    /// </summary>
    /// <param name="editor">The editor control to verify.</param>
    /// <param name="expectedProperties">The property names that should be present.</param>
    public static void AssertHasProperties(this EditorControl editor, params string[] expectedProperties)
    {
        editor.Should().NotBeNull("EditorControl should not be null");

        foreach (var prop in expectedProperties)
        {
            var control = editor.FindControlForProperty(prop);
            control.Should().NotBeNull($"EditorControl should contain field for property '{prop}'");
        }
    }
}
