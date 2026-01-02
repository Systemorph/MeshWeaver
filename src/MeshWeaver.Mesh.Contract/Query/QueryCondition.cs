namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Represents a single query comparison condition.
/// </summary>
/// <param name="Selector">Property path to compare (e.g., "name" or "address.city")</param>
/// <param name="Operator">The comparison operator</param>
/// <param name="Values">One or more values to compare against</param>
public record QueryCondition(string Selector, QueryOperator Operator, string[] Values)
{
    /// <summary>
    /// Gets the first (or only) value for single-value operators.
    /// </summary>
    public string Value => Values.Length > 0 ? Values[0] : string.Empty;
}
