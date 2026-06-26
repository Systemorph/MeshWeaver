using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>Extension methods for formatting author and article metadata.</summary>
public static class ArticleExtensions
{
    /// <summary>Returns the author's full name, including the middle name when present.</summary>
    /// <param name="author">The author whose name should be formatted.</param>
    /// <returns>First Middle Last, or First Last when no middle name is set.</returns>
    public static string FormatName(this Author author)
    {
        if (string.IsNullOrEmpty(author.MiddleName))
            return $"{author.FirstName} {author.LastName}";
        return $"{author.FirstName} {author.MiddleName} {author.LastName}";
    }

}
