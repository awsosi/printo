using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Printing;

/// <summary>
/// Sends bytes to a queue untouched, with the spooler datatype <c>RAW</c>.
/// </summary>
/// <remarks>
/// Used for ZPL/EPL/PCL — either because the source already is printer language, or because
/// the printer profile selects <see cref="ThermalMode.ZplRaster"/>. Going through the spooler
/// rather than opening the port directly keeps the job visible in the Windows queue, so it
/// can be paused, cancelled and accounted for like any other, and it works identically for a
/// USB and a networked printer.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class RawPrinter
{
    /// <summary>Writes <paramref name="data"/> to <paramref name="queueName"/> as one job.</summary>
    /// <returns>The spooler job id.</returns>
    public static int Send(string queueName, ReadOnlySpan<byte> data, string jobName = "Printo raw job")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (data.IsEmpty)
        {
            throw new ArgumentException("nothing to send", nameof(data));
        }

        if (!Win32Print.OpenPrinter(queueName, out var printer, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"could not open printer '{queueName}' (win32 error {Marshal.GetLastWin32Error()})");
        }

        try
        {
            var info = new Win32Print.DocInfo
            {
                Size = Marshal.SizeOf<Win32Print.DocInfo>(),
                DocName = jobName,
                DataType = "RAW",
            };

            var jobId = Win32Print.StartDocPrinter(printer, 1, ref info);
            if (jobId == 0)
            {
                throw new InvalidOperationException(
                    $"{queueName}: StartDocPrinter failed (win32 error {Marshal.GetLastWin32Error()})");
            }

            var pageStarted = false;
            try
            {
                if (!Win32Print.StartPagePrinter(printer))
                {
                    throw new InvalidOperationException(
                        $"{queueName}: StartPagePrinter failed (win32 error {Marshal.GetLastWin32Error()})");
                }

                pageStarted = true;

                var buffer = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data.ToArray(), 0, buffer, data.Length);

                    // WritePrinter can make a partial write; a short write that is not retried
                    // produces a truncated label, which prints as a blank or half a barcode.
                    var offset = 0;
                    while (offset < data.Length)
                    {
                        if (!Win32Print.WritePrinter(printer, buffer + offset, data.Length - offset, out var written)
                            || written <= 0)
                        {
                            throw new InvalidOperationException(
                                $"{queueName}: WritePrinter wrote {written} of {data.Length - offset} bytes " +
                                $"(win32 error {Marshal.GetLastWin32Error()})");
                        }

                        offset += written;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                if (pageStarted)
                {
                    Win32Print.EndPagePrinter(printer);
                }

                Win32Print.EndDocPrinter(printer);
            }

            return jobId;
        }
        finally
        {
            Win32Print.ClosePrinter(printer);
        }
    }
}

/// <summary>Lists the print queues visible to the current session.</summary>
[SupportedOSPlatform("windows")]
public static class PrinterDiscovery
{
    private const int PrinterEnumLocal = 0x00000002;
    private const int PrinterEnumConnections = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string PrinterName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServerName;

        public uint Attributes;
    }

    /// <summary>
    /// Enumerates local queues and this user's connections.
    /// </summary>
    /// <remarks>
    /// A service in session 0 will not see per-user connections such as
    /// <c>\\server\queue</c>; only the tray, running in the user's session, can. That split
    /// is why the agent reports printers from the tray and not from the service.
    /// </remarks>
    public static IReadOnlyList<string> ListQueues()
    {
        const int level = 4;
        Win32Print.EnumPrinters(
            PrinterEnumLocal | PrinterEnumConnections, null, level, IntPtr.Zero, 0, out var needed, out _);

        if (needed <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!Win32Print.EnumPrinters(
                    PrinterEnumLocal | PrinterEnumConnections,
                    null,
                    level,
                    buffer,
                    needed,
                    out _,
                    out var returned))
            {
                throw new InvalidOperationException(
                    $"EnumPrinters failed (win32 error {Marshal.GetLastWin32Error()})");
            }

            var names = new List<string>(returned);
            var size = Marshal.SizeOf<PrinterInfo4>();
            for (var index = 0; index < returned; index++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(buffer + (index * size));
                if (!string.IsNullOrWhiteSpace(info.PrinterName))
                {
                    names.Add(info.PrinterName);
                }
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
