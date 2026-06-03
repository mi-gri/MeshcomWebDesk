using MeshcomWebDesk.Services;

namespace MeshcomWebDesk.Services.Bot;

/// <summary>Returns the current MeshComWebDesk version.</summary>
public class VersionCommand : IBotCommand
{
    // Raw informational version, e.g. "1.12.1-dev+13922dd0af3c…"
    private static readonly string RawVersion =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
                ? attr.InformationalVersion
                : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    // Strip the commit-hash suffix ("+<hash>") so we get e.g. "1.12.1-dev"
    private static readonly string BaseVersion =
        RawVersion.Contains('+') ? RawVersion[..RawVersion.IndexOf('+')] : RawVersion;

    private readonly AppLicenseService _licenseService;

    public VersionCommand(AppLicenseService licenseService)
    {
        _licenseService = licenseService;
    }

    public string Name        => "version";
    public string Description => "MeshComWebDesk Version";

    public Task<string> ExecuteAsync(string[] args, string senderCallsign)
    {
        var callsign = _licenseService.LicensedCallsign;
        var suffix   = callsign != null ? $"+LI-{callsign}" : string.Empty;
        return Task.FromResult($"MeshComWebDesk v{BaseVersion}{suffix}");
    }
}
