using System.Buffers.Binary;
using System.Text;

namespace Printo.Spike.Ipp;

/// <summary>
/// IPP value and delimiter tags (RFC 8010 section 3.5). Only the tags this spike
/// needs to read or write are named; anything else round-trips as raw bytes.
/// </summary>
internal enum IppTag : byte
{
    OperationAttributes = 0x01,
    JobAttributes = 0x02,
    EndOfAttributes = 0x03,
    PrinterAttributes = 0x04,
    UnsupportedAttributes = 0x05,

    Unsupported = 0x10,
    Unknown = 0x12,
    NoValue = 0x13,
    NotSettable = 0x15,
    DeleteAttribute = 0x16,
    AdminDefine = 0x17,

    Integer = 0x21,
    Boolean = 0x22,
    Enum = 0x23,

    OctetString = 0x30,
    DateTime = 0x31,
    Resolution = 0x32,
    RangeOfInteger = 0x33,
    BegCollection = 0x34,
    TextWithLanguage = 0x35,
    NameWithLanguage = 0x36,
    EndCollection = 0x37,

    TextWithoutLanguage = 0x41,
    NameWithoutLanguage = 0x42,
    Keyword = 0x44,
    Uri = 0x45,
    UriScheme = 0x46,
    Charset = 0x47,
    NaturalLanguage = 0x48,
    MimeMediaType = 0x49,
    MemberAttrName = 0x4A,
}

/// <summary>IPP operation ids this spike answers (RFC 8011 section 4.4.15 / PWG 5100.x).</summary>
internal static class IppOperation
{
    public const ushort PrintJob = 0x0002;
    public const ushort PrintUri = 0x0003;
    public const ushort ValidateJob = 0x0004;
    public const ushort CreateJob = 0x0005;
    public const ushort SendDocument = 0x0006;
    public const ushort CancelJob = 0x0008;
    public const ushort GetJobAttributes = 0x0009;
    public const ushort GetJobs = 0x000A;
    public const ushort GetPrinterAttributes = 0x000B;
    public const ushort PausePrinter = 0x0010;
    public const ushort ResumePrinter = 0x0011;
    public const ushort CloseJob = 0x003B;
    public const ushort IdentifyPrinter = 0x003C;

    public static string Name(ushort op) => op switch
    {
        PrintJob => "Print-Job",
        PrintUri => "Print-URI",
        ValidateJob => "Validate-Job",
        CreateJob => "Create-Job",
        SendDocument => "Send-Document",
        CancelJob => "Cancel-Job",
        GetJobAttributes => "Get-Job-Attributes",
        GetJobs => "Get-Jobs",
        GetPrinterAttributes => "Get-Printer-Attributes",
        PausePrinter => "Pause-Printer",
        ResumePrinter => "Resume-Printer",
        CloseJob => "Close-Job",
        IdentifyPrinter => "Identify-Printer",
        _ => $"0x{op:X4}",
    };
}

/// <summary>IPP status codes (RFC 8011 section 13.1).</summary>
internal static class IppStatus
{
    public const ushort Ok = 0x0000;
    public const ushort ClientErrorBadRequest = 0x0400;
    public const ushort ClientErrorNotFound = 0x0406;
    public const ushort ServerErrorOperationNotSupported = 0x0501;
}

/// <summary>One value of an attribute, kept as raw bytes so nothing is lost in translation.</summary>
internal sealed class IppValue(IppTag tag, byte[] raw)
{
    public IppTag Tag { get; } = tag;
    public byte[] Raw { get; } = raw;

    /// <summary>Members of a collection value, populated when <see cref="Tag"/> is BegCollection.</summary>
    public List<IppAttribute> Members { get; } = [];

    public int AsInt() => Raw.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(Raw) : 0;

    public string AsText() => Encoding.UTF8.GetString(Raw);

    /// <summary>Human-readable rendering used by the spike log; the whole point of the exercise.</summary>
    public string Display() => Tag switch
    {
        IppTag.Integer or IppTag.Enum => AsInt().ToString(),
        IppTag.Boolean => Raw.Length == 1 && Raw[0] != 0 ? "true" : "false",
        IppTag.RangeOfInteger when Raw.Length == 8 =>
            $"{BinaryPrimitives.ReadInt32BigEndian(Raw)}-{BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(4))}",
        IppTag.Resolution when Raw.Length == 9 =>
            $"{BinaryPrimitives.ReadInt32BigEndian(Raw)}x{BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(4))}" +
            (Raw[8] == 3 ? "dpi" : Raw[8] == 4 ? "dpcm" : $"units{Raw[8]}"),
        IppTag.BegCollection => "{" + string.Join(", ", Members.Select(m => m.Display())) + "}",
        IppTag.NoValue => "(no-value)",
        IppTag.Unknown => "(unknown)",
        IppTag.Unsupported => "(unsupported)",
        IppTag.OctetString or IppTag.DateTime => Convert.ToHexString(Raw),
        _ => AsText(),
    };
}

/// <summary>A named attribute with one or more values.</summary>
internal sealed class IppAttribute(string name)
{
    public string Name { get; } = name;
    public List<IppValue> Values { get; } = [];

    public string Display() => $"{Name}={string.Join("|", Values.Select(v => v.Display()))}";

    public string? FirstText() => Values.Count > 0 ? Values[0].AsText() : null;
    public int? FirstInt() => Values.Count > 0 ? Values[0].AsInt() : null;
}

/// <summary>An attribute group introduced by a delimiter tag.</summary>
internal sealed class IppGroup(IppTag tag)
{
    public IppTag Tag { get; } = tag;
    public List<IppAttribute> Attributes { get; } = [];

    public IppAttribute? Find(string name) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>A decoded IPP request or response plus its document data.</summary>
internal sealed class IppMessage
{
    public byte VersionMajor { get; set; } = 1;
    public byte VersionMinor { get; set; } = 1;

    /// <summary>Operation id on a request, status code on a response.</summary>
    public ushort Code { get; set; }

    public int RequestId { get; set; }
    public List<IppGroup> Groups { get; } = [];

    /// <summary>Everything after end-of-attributes — the print data, when there is any.</summary>
    public byte[] Data { get; set; } = [];

    public IppGroup? Operation => Groups.FirstOrDefault(g => g.Tag == IppTag.OperationAttributes);

    public IppGroup AddGroup(IppTag tag)
    {
        var group = new IppGroup(tag);
        Groups.Add(group);
        return group;
    }
}
