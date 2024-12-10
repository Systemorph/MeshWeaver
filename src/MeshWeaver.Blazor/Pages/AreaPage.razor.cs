using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages
{
    public partial class AreaPage
    {
        private LayoutAreaControl ViewModel { get; set; }

        [Inject]
        private NavigationManager Navigation { get; set; }

        [Parameter]
        public string AddressId { get; set; }
        [Parameter]
        public string AddressType { get; set; }
        [Parameter]
        public string Environment { get; set; }

        [Parameter]
        public string Area { get; set; }

        [Parameter]
        public string Id
        {
            get;
            set;
        }

        private string PageTitle { get; set; }

        [Parameter(CaptureUnmatchedValues = true)]
        public IReadOnlyDictionary<string, object> Options { get; set; } = ImmutableDictionary<string, object>.Empty;
        private object Address =>
            AddressType switch
            {
                ApplicationAddress.TypeName => new ApplicationAddress(AddressId),
                NotebookAddress.TypeName => new NotebookAddress { Id = Id },

                _ => throw new NotSupportedException($"Address type '{AddressType}' is not supported.")
            };

        private LayoutAreaReference Reference { get; set; }
        protected override async Task OnParametersSetAsync()
        {
            await base.OnParametersSetAsync();

            var id = (string)WorkspaceReference.Decode(Id);
            var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
            if (!string.IsNullOrEmpty(query))
                id += "?" + query;

            Reference = new((string)WorkspaceReference.Decode(Area))
            {
                Id = id,
            };



            ViewModel = new(Address, Reference)
            {
                ShowProgress = true,
            };

        }


    }
}
