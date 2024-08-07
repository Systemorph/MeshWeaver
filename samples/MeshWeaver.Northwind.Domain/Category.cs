using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain
{
    /// <summary>
    /// Represents a category of products within the Northwind domain. This class is used to categorize products for easier management and retrieval.
    /// </summary>
    /// <param name="CategoryId">The unique identifier for the category. Serves as the primary key.</param>
    /// <param name="CategoryName">The name of the category.</param>
    /// <param name="Description">A description of the category.</param>
    /// <param name="Picture">A string representation of a picture associated with the category, potentially a URL or a base64 encoded image.</param>
    /// <remarks>
    /// This record implements the <see cref="INamed"/> interface, which means it provides a DisplayName property that returns the CategoryName.
    /// </remarks>
    /// <seealso cref="INamed"/>
    public record Category(
        [property: Key] int CategoryId,
        string CategoryName,
        string Description,
        string Picture
    ) : INamed
    {
        string INamed.DisplayName => CategoryName;
    }
}
