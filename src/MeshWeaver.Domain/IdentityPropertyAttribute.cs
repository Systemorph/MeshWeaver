namespace MeshWeaver.Domain;

/// <summary>
/// The <see cref="IdentityPropertyAttribute"/> marks a property of an entity class to be part of the identity. This identity is used i.e. in the import of data,
/// in the calculation of generic sums and aggregation.
/// </summary>
/// <conceptualLink target="799cb1b4-2638-49fb-827a-43131d364f06" />
/// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02" />
/// <conceptualLink target="352681E2-A696-48BE-BDBB-0962821F0238" />
/// <remarks>IMPORTANT: this is sealed on purpose, do not change this. Otherwise IdentityEqualityComparer must be adapted!</remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IdentityPropertyAttribute : Attribute { }
