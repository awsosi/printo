using System.Globalization;
using System.Text;

namespace Printo.Agent.Ipp;

/// <summary>One media size the queue offers, in hundredths of a millimetre.</summary>
/// <remarks>
/// The unit is IPP's, not ours: <c>media-col</c> dimensions are hundredths of a millimetre
/// (PWG 5100.7), so the conversion happens once, here, at the edge.
/// </remarks>
internal readonly record struct IppMedia(string Name, int WidthHundredthsMm, int HeightHundredthsMm)
{
    /// <summary>Builds a PWG self-describing media name from a size in millimetres.</summary>
    public static IppMedia FromMillimetres(double widthMm, double heightMm)
    {
        var width = (int)Math.Round(widthMm * 100);
        var height = (int)Math.Round(heightMm * 100);
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"om_{widthMm:0.##}x{heightMm:0.##}mm_{widthMm:0.##}x{heightMm:0.##}mm");

        return new IppMedia(name, width, height);
    }

    public bool IsLabelStock => Name.StartsWith("om_", StringComparison.Ordinal);
}

/// <summary>
/// The printer attribute table the virtual printer answers Get-Printer-Attributes with.
/// </summary>
/// <remarks>
/// Modelled on an IPP Everywhere (PWG 5100.14) self-describing printer, because that is what
/// the inbox Microsoft IPP Class Driver expects to find: nine Get-Printer-Attributes have to be
/// answered in full before Windows will bind a queue at all, and anything less leaves a bare
/// queue with no media list.
///
/// **PDF only.** The M1 spike advertised PDF and PWG Raster together to find out which one
/// Windows would choose; it chose PDF, at native image resolution (plan section 5.0). There is
/// no reason to keep the raster path on offer now that the question is answered - a rasterised
/// job would arrive as a bag of pixels with the page geometry already flattened, which is the
/// input the routing engine is worst at.
/// </remarks>
internal sealed class IppPrinterModel(string printerUri, string printerName, IReadOnlyList<IppMedia> media)
{
    /// <summary>Media every queue offers, whatever else the agent is configured for.</summary>
    internal static readonly IppMedia[] StandardMedia =
    [
        new("iso_a4_210x297mm", 21000, 29700),
        new("na_letter_8.5x11in", 21590, 27940),
    ];

    public string PrinterUri { get; } = printerUri;

    public string PrinterName { get; } = printerName;

    public IReadOnlyList<IppMedia> Media { get; } = media;

    public static string[] SupportedFormats => ["application/pdf", "application/octet-stream"];

    public static string PreferredFormat => "application/pdf";

    /// <summary>
    /// IEEE 1284 device id. Windows reads <c>CMD:</c> when it picks the driver and decides what
    /// it is willing to emit, so it is advertised consistently with document-format-supported.
    /// </summary>
    public string DeviceId =>
        $"MFG:Printo;MDL:{PrinterName};CMD:PDF;CLS:PRINTER;DES:Printo Virtual Printer;";

    /// <summary>
    /// A stable uuid per queue name, so a reinstall presents the same printer rather than a
    /// second one. Derived rather than stored, because the agent has no state at the moment
    /// Windows first asks; two workstations answering alike is harmless when the endpoint is
    /// loopback only.
    /// </summary>
    public string PrinterUuid
    {
        get
        {
            var hash = System.Security.Cryptography.MD5.HashData(
                Encoding.UTF8.GetBytes("printo-virtual-printer:" + PrinterName));
            return $"urn:uuid:{new Guid(hash)}";
        }
    }

    public void WritePrinterAttributes(IppGroup group)
    {
        group.Text("printer-uri-supported", IppTag.Uri, PrinterUri)
             .Keyword("uri-security-supported", "none")
             .Keyword("uri-authentication-supported", "requesting-user-name")
             .Text("printer-name", IppTag.NameWithoutLanguage, PrinterName)
             .Text("printer-info", IppTag.TextWithoutLanguage, "Printo Virtual Printer")
             .Text("printer-location", IppTag.TextWithoutLanguage, "Local")
             .Text("printer-make-and-model", IppTag.TextWithoutLanguage, "Printo Virtual Printer")
             .Text("printer-device-id", IppTag.TextWithoutLanguage, DeviceId)
             .Text("printer-uuid", IppTag.Uri, PrinterUuid)
             .Text("printer-dns-sd-name", IppTag.NameWithoutLanguage, PrinterName)
             .Enum("printer-state", 3) // idle
             .Keyword("printer-state-reasons", "none")
             .Text("printer-state-message", IppTag.TextWithoutLanguage, "Ready")
             .Bool("printer-is-accepting-jobs", true)
             .Integer("queued-job-count", 0)
             .Integer("printer-up-time", (int)(Environment.TickCount64 / 1000))
             .Text("charset-configured", IppTag.Charset, "utf-8")
             .Text("charset-supported", IppTag.Charset, "utf-8")
             .Text("natural-language-configured", IppTag.NaturalLanguage, "en")
             .Text("generated-natural-language-supported", IppTag.NaturalLanguage, "en")
             .Keyword("ipp-versions-supported", "1.0", "1.1", "2.0")
             .Keyword("ipp-features-supported", "ipp-everywhere")
             .Enum(
                 "operations-supported",
                 IppOperation.PrintJob,
                 IppOperation.ValidateJob,
                 IppOperation.CreateJob,
                 IppOperation.SendDocument,
                 IppOperation.CancelJob,
                 IppOperation.GetJobAttributes,
                 IppOperation.GetJobs,
                 IppOperation.GetPrinterAttributes,
                 IppOperation.CloseJob,
                 IppOperation.IdentifyPrinter)
             .Keyword("compression-supported", "none")
             .Keyword("pdl-override-supported", "attempted")
             .Bool("color-supported", true)
             .Integer("multiple-operation-time-out", 120)
             .Keyword("multiple-operation-time-out-action", "process-job")
             .Keyword("identify-actions-default", "sound")
             .Keyword("identify-actions-supported", "sound", "display")
             .Keyword("which-jobs-supported", "completed", "not-completed", "all")
             .Keyword("job-creation-attributes-supported",
                 "copies", "media", "media-col", "orientation-requested", "print-color-mode",
                 "print-quality", "printer-resolution", "sides", "job-name",
                 "multiple-document-handling")
             .Integer("printer-config-change-time", 1)
             .Integer("printer-state-change-time", 1);

        group.Text("document-format-default", IppTag.MimeMediaType, PreferredFormat)
             .Text("document-format-supported", IppTag.MimeMediaType, SupportedFormats)
             .Text("document-format-preferred", IppTag.MimeMediaType, PreferredFormat);

        // Job template attributes.
        group.Integer("copies-default", 1)
             .Range("copies-supported", 1, 99)
             .Keyword("media-default", Media[0].Name)
             .Keyword("media-supported", Media.Select(entry => entry.Name).ToArray())
             .Keyword("media-ready", Media.Select(entry => entry.Name).ToArray())
             .Keyword("media-source-supported", "auto", "main")
             .Keyword("media-type-supported", "stationery", "labels")
             .Integer("media-left-margin-supported", 0)
             .Integer("media-right-margin-supported", 0)
             .Integer("media-top-margin-supported", 0)
             .Integer("media-bottom-margin-supported", 0)
             .Keyword("media-col-supported",
                 "media-size", "media-size-name", "media-type", "media-source",
                 "media-left-margin", "media-right-margin", "media-top-margin", "media-bottom-margin")
             .Enum("orientation-requested-default", 3)
             .Enum("orientation-requested-supported", 3, 4, 5, 6)
             // Colour by default, where the M1 spike advertised monochrome. The capture path
             // is the only chance this product gets at the original: a page the client has
             // already reduced to grey cannot be recovered, and both template matching and the
             // A4 lasers have a use for the colour that would have been thrown away.
             .Keyword("print-color-mode-default", "color")
             .Keyword("print-color-mode-supported", "monochrome", "color", "auto")
             .Enum("print-quality-default", 4)
             .Enum("print-quality-supported", 3, 4, 5)
             .Resolution("printer-resolution-default", 300)
             .Resolution("printer-resolution-supported", 203, 300, 600)
             .Keyword("sides-default", "one-sided")
             .Keyword("sides-supported", "one-sided", "two-sided-long-edge", "two-sided-short-edge")
             .Keyword("output-bin-default", "face-down")
             .Keyword("output-bin-supported", "face-down")
             .Enum("finishings-default", 3)
             .Enum("finishings-supported", 3)
             .Keyword("multiple-document-handling-default", "separate-documents-uncollated-copies")
             .Keyword("multiple-document-handling-supported",
                 "separate-documents-uncollated-copies", "separate-documents-collated-copies")

             // `auto`, which is what the M1 spike advertised when the print path was measured:
             // 22 pages arrived placed at 1:1, with `SCALED` appearing zero times (plan section
             // 5.0a). `none` is the more obviously correct-looking value and is left unproven
             // on purpose - the measured configuration is worth more here than the tidy one,
             // because every millimetre of this product's geometry depends on it.
             .Keyword("print-scaling-default", "auto")
             .Keyword("print-scaling-supported", "none", "auto", "auto-fit", "fill", "fit")
             .Keyword("job-sheets-default", "none")
             .Keyword("job-sheets-supported", "none");

        // PWG Raster capabilities, advertised even though only PDF is offered: an IPP Everywhere
        // client that cannot find them may refuse the queue outright.
        group.Resolution("pwg-raster-document-resolution-supported", 203, 300, 600)
             .Keyword("pwg-raster-document-sheet-back", "normal")
             .Keyword("pwg-raster-document-type-supported", "black_1", "sgray_8", "srgb_8");

        group.Collections("media-col-database", Media.Select(BuildMediaCol));
        group.Collections("media-col-default", [BuildMediaCol(Media[0])]);
    }

    private static IppAttribute[] BuildMediaCol(IppMedia media) =>
    [
        IppGroupExtensions.MemberCollection(
            "media-size",
            IppGroupExtensions.Member("x-dimension", IppTag.Integer, media.WidthHundredthsMm),
            IppGroupExtensions.Member("y-dimension", IppTag.Integer, media.HeightHundredthsMm)),
        IppGroupExtensions.MemberText("media-size-name", IppTag.Keyword, media.Name),
        IppGroupExtensions.MemberText(
            "media-type", IppTag.Keyword, media.IsLabelStock ? "labels" : "stationery"),
        IppGroupExtensions.MemberText("media-source", IppTag.Keyword, "auto"),
        IppGroupExtensions.Member("media-left-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-right-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-top-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-bottom-margin", IppTag.Integer, 0),
    ];

    /// <summary>Identifies the page description language actually delivered, by magic bytes.</summary>
    /// <remarks>
    /// The queue advertises PDF only, so anything else here means an application ignored the
    /// negotiated format. Such a job is refused with the format it really sent named in the
    /// error, rather than spooled and left to fail later as an unreadable document.
    /// </remarks>
    public static (string Format, string Extension) SniffPdl(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
            {
                return ("application/pdf", "pdf");
            }

            var magic = Encoding.ASCII.GetString(data[..4]);
            if (magic is "RaS2" or "2SaR" or "RaST" or "TSaR")
            {
                return ("image/pwg-raster", "pwg");
            }

            if (magic.StartsWith("UNIR", StringComparison.Ordinal))
            {
                return ("image/urf", "urf");
            }

            if (data[0] == 0x50 && data[1] == 0x4B)
            {
                return ("application/oxps", "oxps");
            }

            if (data[0] == 0x1B)
            {
                return ("application/vnd.hp-pcl", "pcl");
            }
        }

        return ("application/octet-stream", "bin");
    }
}
