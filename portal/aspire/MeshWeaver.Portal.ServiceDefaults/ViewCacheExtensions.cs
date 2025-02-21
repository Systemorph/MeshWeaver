using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Data;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Portal.ServiceDefaults;

public static class ViewCacheExtensions
{
    public record CachedView(LayoutAreaReference Reference, string Html);

    public static void RegisterViewCache(this IHostApplicationBuilder builder)
    {

    }
}
