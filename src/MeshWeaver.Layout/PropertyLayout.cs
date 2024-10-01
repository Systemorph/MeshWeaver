namespace MeshWeaver.Layout
{
	/// <summary>
	/// Represents the layout of a property with a system name, display name, and associated control.
	/// </summary>
	/// <param name="SystemName">The system name of the property.</param>
	/// <param name="DisplayName">The display name of the property.</param>
	/// <param name="Control">The UI control associated with the property.</param>
	public record PropertyLayout(string SystemName, string DisplayName, IUiControl Control);
}
