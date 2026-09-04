using System.Text;

namespace Printo.Spike.Ipp;

/// <summary>Which document formats the spike advertises — the variable under test in M1.</summary>
internal enum FormatAdvertisement
{
    /// <summary>PDF only. If Windows still sends PWG Raster here, PDF is simply not on offer.</summary>
    Pdf,

    /// <summary>PWG Raster only — the documented baseline of the IPP Class Driver.</summary>
    Raster,

    /// <summary>Both, with <c>document-format-preferred</c> = application/pdf.</summary>
    Both,
}

/// <summary>
/// The printer attribute table the spike answers Get-Printer-Attributes with. Modelled on an
/// IPP Everywhere (PWG 5100.14) self-describing printer, because that is what the inbox
/// Microsoft IPP Class Driver expects to find; anything less and Windows falls back to a
/// bare queue with no media list.
/// </summary>
internal sealed class PrinterModel(string printerUri, FormatAdvertisement formats)
{
    /// <summary>Media the spike claims to have loaded: A4, Letter and a 100x150 mm label.</summary>
    private static readonly (string Name, int WidthHundredthsMm, int HeightHundredthsMm)[] Media =
    [
        ("iso_a4_210x297mm", 21000, 29700),
        ("na_letter_8.5x11in", 21590, 27940),
        ("om_100x150mm_100x150mm", 10000, 15000),
        ("om_100x200mm_100x200mm", 10000, 20000),
    ];

    public string PrinterUri { get; } = printerUri;

    public FormatAdvertisement Formats { get; } = formats;

    public string[] SupportedFormats => Formats switch
    {
        FormatAdvertisement.Pdf => ["application/pdf", "application/octet-stream"],
        FormatAdvertisement.Raster => ["image/pwg-raster", "application/octet-stream"],
        _ => ["application/pdf", "image/pwg-raster", "application/octet-stream"],
    };

    public string PreferredFormat =>
        Formats == FormatAdvertisement.Raster ? "image/pwg-raster" : "application/pdf";

    /// <summary>
    /// IEEE 1284 device id. Windows reads <c>CMD:</c> when it picks the driver and decides what
    /// it is willing to emit, so it is advertised consistently with document-format-supported.
    /// </summary>
    public string DeviceId
    {
        get
        {
            var commands = Formats switch
            {
                FormatAdvertisement.Pdf => "PDF",
                FormatAdvertisement.Raster => "PWGRaster",
                _ => "PDF,PWGRaster",
            };

            return $"MFG:Printo;MDL:Printo Virtual Printer;CMD:{commands};CLS:PRINTER;" +
                   "DES:Printo Virtual Printer;";
        }
    }

    public void WritePrinterAttributes(IppGroup group)
    {
        group.Text("printer-uri-supported", IppTag.Uri, PrinterUri)
             .Keyword("uri-security-supported", "none")
             .Keyword("uri-authentication-supported", "requesting-user-name")
             .Text("printer-name", IppTag.NameWithoutLanguage, "Printo")
             .Text("printer-info", IppTag.TextWithoutLanguage, "Printo Virtual Printer")
             .Text("printer-location", IppTag.TextWithoutLanguage, "Local")
             .Text("printer-make-and-model", IppTag.TextWithoutLanguage, "Printo Virtual Printer")
             .Text("printer-device-id", IppTag.TextWithoutLanguage, DeviceId)
             .Text("printer-uuid", IppTag.Uri, "urn:uuid:6f2b9a10-5c1d-4c9e-9a11-7f0c3f5d2a41")
             .Text("printer-dns-sd-name", IppTag.NameWithoutLanguage, "Printo")
             .Enum("printer-state", 3) // idle
             .Keyword("printer-state-reasons", "none")
             .Text("printer-state-message", IppTag.TextWithoutLanguage, "Ready")
             .Bool("printer-is-accepting-jobs", true)
             .Integer("queued-job-count", 0)
             .Integer("printer-up-time", (int)Environment.TickCount64 / 1000)
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

        // Document formats — the answer M1 is after.
        group.Text("document-format-default", IppTag.MimeMediaType, PreferredFormat)
             .Text("document-format-supported", IppTag.MimeMediaType, SupportedFormats)
             .Text("document-format-preferred", IppTag.MimeMediaType, PreferredFormat);

        // Job template attributes.
        group.Integer("copies-default", 1)
             .Range("copies-supported", 1, 99)
             .Keyword("media-default", "iso_a4_210x297mm")
             .Keyword("media-supported", Media.Select(m => m.Name).ToArray())
             .Keyword("media-ready", "iso_a4_210x297mm", "om_100x150mm_100x150mm")
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
             .Keyword("print-color-mode-default", "monochrome")
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
             .Keyword("print-scaling-default", "auto")
             .Keyword("print-scaling-supported", "auto", "auto-fit", "fill", "fit", "none")
             .Keyword("job-sheets-default", "none")
             .Keyword("job-sheets-supported", "none");

        // PWG Raster capabilities. Advertised even in PDF-only mode: an IPP Everywhere client
        // that cannot find them may refuse the queue outright, which would confuse the result.
        group.Resolution("pwg-raster-document-resolution-supported", 203, 300, 600)
             .Keyword("pwg-raster-document-sheet-back", "normal")
             .Keyword("pwg-raster-document-type-supported",
                 "black_1", "sgray_8", "srgb_8");

        group.Collections("media-col-database", Media.Select(BuildMediaCol));
        group.Collections("media-col-default", [BuildMediaCol(Media[0])]);
    }

    private static IppAttribute[] BuildMediaCol(
        (string Name, int WidthHundredthsMm, int HeightHundredthsMm) media) =>
    [
        IppGroupExtensions.MemberCollection(
            "media-size",
            IppGroupExtensions.Member("x-dimension", IppTag.Integer, media.WidthHundredthsMm),
            IppGroupExtensions.Member("y-dimension", IppTag.Integer, media.HeightHundredthsMm)),
        IppGroupExtensions.MemberText("media-size-name", IppTag.Keyword, media.Name),
        IppGroupExtensions.MemberText(
            "media-type", IppTag.Keyword, media.Name.StartsWith("om_", StringComparison.Ordinal) ? "labels" : "stationery"),
        IppGroupExtensions.MemberText("media-source", IppTag.Keyword, "auto"),
        IppGroupExtensions.Member("media-left-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-right-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-top-margin", IppTag.Integer, 0),
        IppGroupExtensions.Member("media-bottom-margin", IppTag.Integer, 0),
    ];

    /// <summary>Identifies the page description language actually delivered, by magic bytes.</summary>
    public static (string Format, string Extension) SniffPdl(byte[] data)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
            {
                return ("application/pdf", "pdf");
            }

            var magic = Encoding.ASCII.GetString(data, 0, 4);
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
