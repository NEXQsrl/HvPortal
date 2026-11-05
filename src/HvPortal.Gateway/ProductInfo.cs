using System.Reflection;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace HvPortal.Gateway;

/// <summary>Product version and hosting mode, as shown to administrators (sidebar, update guide, system info).</summary>
public static class ProductInfo
{
    /// <summary>Informational version without build metadata (e.g. "0.3.0", not "0.3.0+abc123").</summary>
    public static string Version { get; } =
        (typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(ProductInfo).Assembly.GetName().Version?.ToString()
         ?? "0.0.0").Split('+')[0];

    /// <summary>True when started by the Service Control Manager (production install), false for a console/dev start.</summary>
    public static bool RunningAsService { get; } = WindowsServiceHelpers.IsWindowsService();
}
