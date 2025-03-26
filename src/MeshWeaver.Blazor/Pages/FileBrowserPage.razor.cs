using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MeshWeaver.Blazor.Pages;

public partial class FileBrowserPage
{
    private void CollectionChanged(string collection)
    {
        Collection = collection;
        InvokeAsync(StateHasChanged);
    }
}
