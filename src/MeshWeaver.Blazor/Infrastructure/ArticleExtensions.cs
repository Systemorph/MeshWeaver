using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;

namespace MeshWeaver.Blazor.Infrastructure;

public static class ArticleExtensions
{
    public static string FormatName(this Author author)
    {
        if (string.IsNullOrEmpty(author.MiddleName))
            return $"{author.FirstName} {author.LastName}";
        return $"{author.FirstName} {author.MiddleName} {author.LastName}";
    }

}
