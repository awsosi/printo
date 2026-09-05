using Printo.Agent.Render;

namespace Printo.Agent.Printing;

/// <summary>One page handed to a device, with everything needed to explain the output.</summary>
public sealed class PrintedPage
{
    public required ComposedPage Composed { get; init; }

    public required int Copies { get; init; }

    /// <summary>Source page number within the original document, for the job record.</summary>
    public required int PageNumber { get; init; }
}

/// <summary>
/// Somewhere a composed page can be sent.
/// </summary>
/// <remarks>
/// The abstraction exists so the print path is asserted end to end without a printer: the
/// same code that drives a real queue drives <see cref="RecordingPrinterDevice"/> in tests.
/// Hardware verification is a separate, manual matrix — this is what makes everything up to
/// the last inch testable.
/// </remarks>
public interface IPrinterDevice : IDisposable
{
    string Name { get; }

    /// <summary>What the device can do. Queried once when the device is opened.</summary>
    PrinterCapabilities Capabilities { get; }

    void StartDocument(string jobName);

    void PrintPage(PrintedPage page);

    void EndDocument();
}

/// <summary>
/// A device that records what would have been printed.
/// </summary>
/// <remarks>
/// The virtual printer harness from the plan's test strategy. It keeps the composed rasters,
/// so a test can assert the geometry of what reached the device — not merely that a print call
/// was made.
/// </remarks>
public sealed class RecordingPrinterDevice : IPrinterDevice
{
    private readonly List<PrintedPage> pages = [];

    public RecordingPrinterDevice(string name, PrinterCapabilities capabilities)
    {
        Name = name;
        Capabilities = capabilities;
    }

    /// <summary>A borderless 100x150 mm thermal printer at 203 dpi — the product default.</summary>
    public static RecordingPrinterDevice Thermal(string name = "Test-Thermal") =>
        new(name, new PrinterCapabilities
        {
            QueueName = name,
            DpiX = 203,
            DpiY = 203,
            PhysicalWidthMm = 100,
            PhysicalHeightMm = 150,
            OffsetXMm = 0,
            OffsetYMm = 0,
            PrintableWidthMm = 100,
            PrintableHeightMm = 150,
        });

    /// <summary>An A4 laser at 600 dpi with a 4 mm unprintable border.</summary>
    public static RecordingPrinterDevice A4Laser(string name = "Test-A4") =>
        new(name, new PrinterCapabilities
        {
            QueueName = name,
            DpiX = 600,
            DpiY = 600,
            PhysicalWidthMm = 210,
            PhysicalHeightMm = 297,
            OffsetXMm = 4,
            OffsetYMm = 4,
            PrintableWidthMm = 202,
            PrintableHeightMm = 289,
        });

    public string Name { get; }

    public PrinterCapabilities Capabilities { get; }

    public IReadOnlyList<PrintedPage> Pages => pages;

    public string? JobName { get; private set; }

    public bool DocumentOpen { get; private set; }

    public int DocumentsCompleted { get; private set; }

    public void StartDocument(string jobName)
    {
        if (DocumentOpen)
        {
            throw new InvalidOperationException($"{Name}: a document is already open");
        }

        JobName = jobName;
        DocumentOpen = true;
    }

    public void PrintPage(PrintedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!DocumentOpen)
        {
            throw new InvalidOperationException($"{Name}: PrintPage before StartDocument");
        }

        pages.Add(page);
    }

    public void EndDocument()
    {
        if (!DocumentOpen)
        {
            throw new InvalidOperationException($"{Name}: EndDocument without StartDocument");
        }

        DocumentOpen = false;
        DocumentsCompleted++;
    }

    public void Dispose()
    {
        // A device that is disposed mid-document has lost the job; surfacing it as a failed
        // document rather than silently discarding it is what lets the spool retry.
        DocumentOpen = false;
    }
}
