namespace OpenSmc.Domain.Abstractions.Attributes
{
    /// <summary>
    /// The <see cref="TypeWithInterfaceAttribute"/> links the implementation of an entity to an interface
    /// describing the entity. Entities marked with this attribute can be used with their interface as
    /// type in navigation properties.
    /// </summary>
    /// <example>
    /// This example shows how to apply the attribute
    /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Domain\TypesWithInterfacesSnippets.cs" language="cs" region="WithInterface" />
    /// </example>
    /// <example>
    /// For a complete walk-through with examples how to use this attribute, see <conceptualLink target="d633bdfb-943b-451a-ac8b-5f9da028e8f9#typesWithInterfaces" />
    /// </example>
    /// <conceptualLink target="d633bdfb-943b-451a-ac8b-5f9da028e8f9" />
    public class TypeWithInterfaceAttribute : Attribute
    {
        public TypeWithInterfaceAttribute(Type interfaceType)
        {
            InterfaceType = interfaceType;
        }
        /// <summary>
        /// If the system entity belongs to an interface, the interface type has to be set.
        /// </summary>
        public Type InterfaceType;
    }
}