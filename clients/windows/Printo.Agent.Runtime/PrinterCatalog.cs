using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;

namespace Printo.Agent.Runtime;

/// <summary>
/// Maps a routing decision's target onto a real printer.
/// </summary>
/// <remarks>
/// Rules speak in roles (`A4`, `THERMAL`) and aliases, never in queue names: the same rule
/// bundle is published to every workstation, and each has different printers installed. This
/// is where that indirection is resolved, from the per-machine printer map.
/// </remarks>
public interface IPrinterCatalog
{
    /// <summary>The profile for a route, or <c>null</c> when the machine has no printer for it.</summary>
    PrinterProfile? Resolve(string route);

    /// <summary>Opens a device for the profile at the given media size.</summary>
    IPrinterDevice Open(PrinterProfile profile, MediaSize media);
}

/// <summary>
/// A catalog built from the agent's configured printer profiles.
/// </summary>
public sealed class PrinterCatalog(IReadOnlyList<PrinterProfile> profiles, Func<PrinterProfile, MediaSize, IPrinterDevice> factory)
    : IPrinterCatalog
{
    private readonly IReadOnlyList<PrinterProfile> profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));

    private readonly Func<PrinterProfile, MediaSize, IPrinterDevice> factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Builds a catalog that opens real Windows queues.</summary>
    public static PrinterCatalog ForWindows(IReadOnlyList<PrinterProfile> profiles) =>
        new(profiles, (profile, media) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows print queues are only available on Windows");
            }

            return new WindowsPrinterDevice(profile.QueueName, media);
        });

    public PrinterProfile? Resolve(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        // An alias is more specific than a role, so it is matched first: a rule that names a
        // particular printer must not be satisfied by whatever happens to hold that role.
        var byAlias = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Alias, route, StringComparison.OrdinalIgnoreCase));
        if (byAlias is not null)
        {
            return byAlias;
        }

        var role = route switch
        {
            RoutingProfileRules.RouteA4 => PrinterRole.A4,
            RoutingProfileRules.RouteThermal => PrinterRole.Thermal,
            _ => (PrinterRole?)null,
        };

        return role is null
            ? null
            : profiles.FirstOrDefault(profile => profile.Role == role.Value);
    }

    public IPrinterDevice Open(PrinterProfile profile, MediaSize media) => factory(profile, media);
}
