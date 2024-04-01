namespace OpenSmc.Domain.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class CategoryAttribute<T>(string category = null) : Attribute
{
    public string Category { get; } = category ?? typeof(T).Name;
}

//Use System.ComponentModel.CategoryAttribute

//[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
//public class CategoryAttribute(string category) : Attribute
//{
//    public string Category { get; } = category;
//}