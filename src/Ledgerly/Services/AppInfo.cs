using System.Reflection;

namespace Ledgerly.Services;

/// <summary>Which build is running: its version (set by the release workflow) and release channel.</summary>
public class AppInfo(IConfiguration configuration)
{
    /// <summary>The build version, e.g. "1.0.42" or "1.0.43-beta", without the source commit suffix.</summary>
    public string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>"Production", "Beta", or "Development" when running from source.</summary>
    public string Channel { get; } = configuration["Ledgerly:Channel"] is { Length: > 0 } channel ? channel : "Development";

    public bool IsBeta => Channel.Equals("Beta", StringComparison.OrdinalIgnoreCase);

    public bool IsProduction => Channel.Equals("Production", StringComparison.OrdinalIgnoreCase);
}
