namespace MeshWeaver.Domain;

/// <summary>
/// Marks a property as hidden so it is not displayed in generated UI.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class NotVisibleAttribute : Attribute { }
