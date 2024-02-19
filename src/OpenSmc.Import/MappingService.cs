using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;

namespace OpenSmc.Import
{
#pragma warning disable 4014

    public record ImportState
    {
        public IDictionary<object, object> ValidationCache { get; set; }
    }
}