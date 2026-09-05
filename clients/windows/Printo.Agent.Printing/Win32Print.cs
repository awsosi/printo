using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Printing;

/// <summary>Win32 spooler and GDI entry points used by the print path.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Win32Print
{
    public const int DmOrientation = 0x00000001;
    public const int DmPaperSize = 0x00000002;
    public const int DmPaperLength = 0x00000004;
    public const int DmPaperWidth = 0x00000008;
    public const int DmCopies = 0x00000100;
    public const int DmPrintQuality = 0x00000400;
    public const int DmYResolution = 0x00002000;

    public const short DmPaperUser = 256;
    public const short DmOrientPortrait = 1;
    public const short DmOrientLandscape = 2;

    public const int DmModifyBuffer = 8;   // DM_IN_BUFFER
    public const int DmCopyBuffer = 2;     // DM_OUT_BUFFER

    // GetDeviceCaps indices.
    public const int HorzRes = 8;
    public const int VertRes = 10;
    public const int LogPixelsX = 88;
    public const int LogPixelsY = 90;
    public const int PhysicalWidth = 110;
    public const int PhysicalHeight = 111;
    public const int PhysicalOffsetX = 112;
    public const int PhysicalOffsetY = 113;

    public const int SrcCopy = 0x00CC0020;
    public const int DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public short Orientation;
        public short PaperSize;

        /// <summary>Tenths of a millimetre.</summary>
        public short PaperLength;

        /// <summary>Tenths of a millimetre.</summary>
        public short PaperWidth;

        public short Scale;
        public short Copies;
        public short DefaultSource;
        public short PrintQuality;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;

        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DocInfo
    {
        public int Size;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DocName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Output;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DataType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DocInfoGdi
    {
        public int Size;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DocName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Output;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DataType;

        public int Type;
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [LibraryImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClosePrinter(IntPtr printer);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int DocumentProperties(
        IntPtr window,
        IntPtr printer,
        string deviceName,
        IntPtr devModeOutput,
        IntPtr devModeInput,
        int mode);

    // DllImport rather than LibraryImport: the source generator cannot marshal a struct
    // containing string fields as an `in` parameter (SYSLIB1051), and DOC_INFO_1 is exactly
    // that. Classic marshalling handles it correctly.
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo docInfo);

    [LibraryImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDocPrinter(IntPtr printer);

    [LibraryImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartPagePrinter(IntPtr printer);

    [LibraryImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPagePrinter(IntPtr printer);

    [LibraryImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WritePrinter(IntPtr printer, IntPtr buffer, int count, out int written);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumPrinters(
        int flags,
        string? name,
        int level,
        IntPtr buffer,
        int size,
        out int needed,
        out int returned);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateDC(string driver, string device, IntPtr output, IntPtr devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDeviceCaps")]
    public static partial int GetDeviceCaps(IntPtr dc, int index);

    // See the note on StartDocPrinter: DOCINFO also carries strings.
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartDoc(IntPtr dc, ref DocInfoGdi docInfo);

    [LibraryImport("gdi32.dll", EntryPoint = "EndDoc", SetLastError = true)]
    public static partial int EndDoc(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "StartPage", SetLastError = true)]
    public static partial int StartPage(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "EndPage", SetLastError = true)]
    public static partial int EndPage(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits", SetLastError = true)]
    public static partial int StretchDIBits(
        IntPtr dc,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        IntPtr bits,
        in BitmapInfoHeader info,
        int usage,
        int rasterOperation);
}
