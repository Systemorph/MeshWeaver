using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Blazor
{
    public partial class BlazorView<TViewModel> : IDisposable
    {
        protected List<IDisposable> Disposables { get; } = new();

        public void Dispose()
        {
            foreach (var d in Disposables)
            {
                d.Dispose();
            }
        }
    }
}
