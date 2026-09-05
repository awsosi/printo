using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Render;

namespace Printo.Agent.Printing;

/// <summary>
/// A real Windows print queue, driven through GDI.
/// </summary>
/// <remarks>
/// The composed raster goes to <c>StretchDIBits</c> as a top-down 32bpp DIB, which is the
/// layout <see cref="RasterImage"/> already uses — so the bytes asserted by the render-diff
/// tests are the bytes the driver receives, with no conversion in between.
///
/// Custom media is set through the queue's own DEVMODE rather than by picking a named form:
/// label stock is a free <c>WxH mm</c> value in this product, and no driver has a form for
/// every size a site might load. The DEVMODE buffer is read from the driver, modified in
/// place and validated by the driver before use, so driver-private data past the public
/// structure is preserved — copying only the public part is how custom sizes silently fail
/// on some vendors' drivers.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterDevice : IPrinterDevice
{
    private IntPtr deviceContext;

    private bool documentOpen;

    public WindowsPrinterDevice(string queueName, MediaSize? media = null, bool landscape = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        Name = queueName;

        var devMode = IntPtr.Zero;
        try
        {
            devMode = BuildDevMode(queueName, media, landscape);
            deviceContext = Win32Print.CreateDC("WINSPOOL", queueName, IntPtr.Zero, devMode);
            if (deviceContext == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"could not open a device context for '{queueName}' " +
                    $"(win32 error {Marshal.GetLastWin32Error()})");
            }

            Capabilities = ReadCapabilities(queueName, deviceContext);
        }
        finally
        {
            if (devMode != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(devMode);
            }
        }
    }

    public string Name { get; }

    public PrinterCapabilities Capabilities { get; }

    /// <summary>Reads a queue's capabilities without starting a job.</summary>
    public static PrinterCapabilities Query(string queueName, MediaSize? media = null)
    {
        using var device = new WindowsPrinterDevice(queueName, media);
        return device.Capabilities;
    }

    public void StartDocument(string jobName)
    {
        ObjectDisposedException.ThrowIf(deviceContext == IntPtr.Zero, this);
        if (documentOpen)
        {
            throw new InvalidOperationException($"{Name}: a document is already open");
        }

        var info = new Win32Print.DocInfoGdi
        {
            Size = Marshal.SizeOf<Win32Print.DocInfoGdi>(),
            DocName = jobName,
        };

        if (Win32Print.StartDoc(deviceContext, ref info) <= 0)
        {
            throw new InvalidOperationException(
                $"{Name}: StartDoc failed (win32 error {Marshal.GetLastWin32Error()})");
        }

        documentOpen = true;
    }

    public void PrintPage(PrintedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ObjectDisposedException.ThrowIf(deviceContext == IntPtr.Zero, this);
        if (!documentOpen)
        {
            throw new InvalidOperationException($"{Name}: PrintPage before StartDocument");
        }

        var raster = page.Composed.Raster;

        // Negative height marks the DIB top-down, matching RasterImage's layout. Getting this
        // wrong prints the page upside down, which is exactly the kind of defect that only
        // shows up on paper.
        var header = new Win32Print.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Win32Print.BitmapInfoHeader>(),
            Width = raster.Width,
            Height = -raster.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = (uint)(raster.Stride * raster.Height),
        };

        // The composed raster already covers the whole physical sheet at device resolution,
        // so it maps 1:1 onto the device's physical extent - no further scaling here.
        var deviceWidth = Win32Print.GetDeviceCaps(deviceContext, Win32Print.PhysicalWidth);
        var deviceHeight = Win32Print.GetDeviceCaps(deviceContext, Win32Print.PhysicalHeight);
        var offsetX = Win32Print.GetDeviceCaps(deviceContext, Win32Print.PhysicalOffsetX);
        var offsetY = Win32Print.GetDeviceCaps(deviceContext, Win32Print.PhysicalOffsetY);

        var pinned = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        try
        {
            for (var copy = 0; copy < Math.Max(1, page.Copies); copy++)
            {
                if (Win32Print.StartPage(deviceContext) <= 0)
                {
                    throw new InvalidOperationException(
                        $"{Name}: StartPage failed (win32 error {Marshal.GetLastWin32Error()})");
                }

                // GDI's origin is the printable area, so the physical offset is subtracted:
                // the sheet raster starts at the paper edge, which sits above and left of it.
                var result = Win32Print.StretchDIBits(
                    deviceContext,
                    -offsetX,
                    -offsetY,
                    deviceWidth,
                    deviceHeight,
                    0,
                    0,
                    raster.Width,
                    raster.Height,
                    pinned.AddrOfPinnedObject(),
                    in header,
                    Win32Print.DibRgbColors,
                    Win32Print.SrcCopy);

                if (result == 0)
                {
                    throw new InvalidOperationException(
                        $"{Name}: StretchDIBits failed (win32 error {Marshal.GetLastWin32Error()})");
                }

                if (Win32Print.EndPage(deviceContext) <= 0)
                {
                    throw new InvalidOperationException(
                        $"{Name}: EndPage failed (win32 error {Marshal.GetLastWin32Error()})");
                }
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    public void EndDocument()
    {
        if (!documentOpen)
        {
            throw new InvalidOperationException($"{Name}: EndDocument without StartDocument");
        }

        documentOpen = false;
        if (Win32Print.EndDoc(deviceContext) <= 0)
        {
            throw new InvalidOperationException(
                $"{Name}: EndDoc failed (win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    public void Dispose()
    {
        if (deviceContext == IntPtr.Zero)
        {
            return;
        }

        if (documentOpen)
        {
            // Abandon rather than commit: a half-written document must not reach the paper.
            Win32Print.EndDoc(deviceContext);
            documentOpen = false;
        }

        Win32Print.DeleteDC(deviceContext);
        deviceContext = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private static PrinterCapabilities ReadCapabilities(string queueName, IntPtr dc)
    {
        var dpiX = Win32Print.GetDeviceCaps(dc, Win32Print.LogPixelsX);
        var dpiY = Win32Print.GetDeviceCaps(dc, Win32Print.LogPixelsY);
        if (dpiX <= 0 || dpiY <= 0)
        {
            throw new InvalidOperationException($"{queueName}: driver reported {dpiX}x{dpiY} dpi");
        }

        var physicalWidth = Win32Print.GetDeviceCaps(dc, Win32Print.PhysicalWidth);
        var physicalHeight = Win32Print.GetDeviceCaps(dc, Win32Print.PhysicalHeight);
        var offsetX = Win32Print.GetDeviceCaps(dc, Win32Print.PhysicalOffsetX);
        var offsetY = Win32Print.GetDeviceCaps(dc, Win32Print.PhysicalOffsetY);
        var printableWidth = Win32Print.GetDeviceCaps(dc, Win32Print.HorzRes);
        var printableHeight = Win32Print.GetDeviceCaps(dc, Win32Print.VertRes);

        static double ToMm(int dots, int dpi) => dpi > 0 ? dots * 25.4 / dpi : 0;

        return new PrinterCapabilities
        {
            QueueName = queueName,
            DpiX = dpiX,
            DpiY = dpiY,
            PhysicalWidthMm = ToMm(physicalWidth, dpiX),
            PhysicalHeightMm = ToMm(physicalHeight, dpiY),
            OffsetXMm = ToMm(offsetX, dpiX),
            OffsetYMm = ToMm(offsetY, dpiY),
            PrintableWidthMm = ToMm(printableWidth, dpiX),
            PrintableHeightMm = ToMm(printableHeight, dpiY),
        };
    }

    /// <summary>
    /// Reads the queue's DEVMODE, applies the requested media, and lets the driver validate it.
    /// </summary>
    private static IntPtr BuildDevMode(string queueName, MediaSize? media, bool landscape)
    {
        if (media is null && !landscape)
        {
            return IntPtr.Zero;
        }

        if (!Win32Print.OpenPrinter(queueName, out var printer, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"could not open printer '{queueName}' (win32 error {Marshal.GetLastWin32Error()})");
        }

        try
        {
            var size = Win32Print.DocumentProperties(IntPtr.Zero, printer, queueName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
            {
                throw new InvalidOperationException(
                    $"{queueName}: driver reported a DEVMODE size of {size}");
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (Win32Print.DocumentProperties(
                        IntPtr.Zero, printer, queueName, buffer, IntPtr.Zero, Win32Print.DmCopyBuffer) < 0)
                {
                    throw new InvalidOperationException($"{queueName}: could not read the current DEVMODE");
                }

                var devMode = Marshal.PtrToStructure<Win32Print.DevMode>(buffer);

                if (media is not null)
                {
                    devMode.PaperSize = Win32Print.DmPaperUser;

                    // DEVMODE measures paper in tenths of a millimetre.
                    devMode.PaperWidth = (short)Math.Round(media.WidthMm * 10);
                    devMode.PaperLength = (short)Math.Round(media.HeightMm * 10);
                    devMode.Fields |= Win32Print.DmPaperSize | Win32Print.DmPaperWidth | Win32Print.DmPaperLength;
                }

                devMode.Orientation = landscape ? Win32Print.DmOrientLandscape : Win32Print.DmOrientPortrait;
                devMode.Fields |= Win32Print.DmOrientation;

                // Writes only the public part; anything the driver keeps past it stays intact.
                Marshal.StructureToPtr(devMode, buffer, fDeleteOld: false);

                // Let the driver reconcile the request with what it can actually do.
                if (Win32Print.DocumentProperties(
                        IntPtr.Zero,
                        printer,
                        queueName,
                        buffer,
                        buffer,
                        Win32Print.DmCopyBuffer | Win32Print.DmModifyBuffer) < 0)
                {
                    throw new InvalidOperationException(
                        $"{queueName}: driver rejected media {media?.ToString() ?? "(default)"}");
                }

                return buffer;
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }
        finally
        {
            Win32Print.ClosePrinter(printer);
        }
    }
}
