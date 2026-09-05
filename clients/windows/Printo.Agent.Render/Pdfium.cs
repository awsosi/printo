using System.Runtime.InteropServices;

namespace Printo.Agent.Render;

/// <summary>
/// The slice of the PDFium C API the agent needs.
/// </summary>
/// <remarks>
/// Declared by hand rather than taken from a managed wrapper: the crop is expressed as a
/// negative render origin, and <c>FPDF_RenderPageBitmap</c>'s <c>start_x</c>/<c>start_y</c>
/// are the only way to say that. Wrappers uniformly hide those behind a "render whole page at
/// size N" call, which would force a render-then-crop and lose sub-pixel placement.
/// </remarks>
internal static partial class Pdfium
{
    private const string Library = "pdfium";

    /// <summary>BGRA, 8 bits per channel — matches a Windows 32bpp DIB byte for byte.</summary>
    public const int FormatBgra = 4;

    /// <summary>Render flag: use anti-aliasing for text and paths (PDFium default is on).</summary>
    public const int RenderAnnotations = 0x01;

    /// <summary>Render flag: grayscale output.</summary>
    public const int RenderGrayscale = 0x08;

    /// <summary>Render flag: no native text rendering, so output is deterministic across hosts.</summary>
    public const int RenderNoNativeText = 0x02;

    /// <summary>Render flag: limit image cache, avoids unbounded memory on large jobs.</summary>
    public const int RenderLimitedImageCache = 0x200;

    [LibraryImport(Library, EntryPoint = "FPDF_InitLibrary")]
    public static partial void InitLibrary();

    [LibraryImport(Library, EntryPoint = "FPDF_DestroyLibrary")]
    public static partial void DestroyLibrary();

    [LibraryImport(Library, EntryPoint = "FPDF_LoadMemDocument64")]
    public static partial IntPtr LoadMemDocument64(IntPtr data, nuint size, IntPtr password);

    [LibraryImport(Library, EntryPoint = "FPDF_CloseDocument")]
    public static partial void CloseDocument(IntPtr document);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageCount")]
    public static partial int GetPageCount(IntPtr document);

    [LibraryImport(Library, EntryPoint = "FPDF_LoadPage")]
    public static partial IntPtr LoadPage(IntPtr document, int pageIndex);

    [LibraryImport(Library, EntryPoint = "FPDF_ClosePage")]
    public static partial void ClosePage(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageWidthF")]
    public static partial float GetPageWidth(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageHeightF")]
    public static partial float GetPageHeight(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDFPage_GetRotation")]
    public static partial int GetPageRotation(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_CreateEx")]
    public static partial IntPtr BitmapCreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_FillRect")]
    public static partial void BitmapFillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_Destroy")]
    public static partial void BitmapDestroy(IntPtr bitmap);

    [LibraryImport(Library, EntryPoint = "FPDF_RenderPageBitmap")]
    public static partial void RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [LibraryImport(Library, EntryPoint = "FPDF_GetLastError")]
    public static partial uint GetLastError();
}

/// <summary>
/// Process-wide PDFium initialisation and serialisation.
/// </summary>
/// <remarks>
/// Two facts about PDFium drive this type:
///
/// 1. Library init is global and not reference-counted. Calling it twice, or tearing it down
///    while a document is open, crashes the process. So it happens exactly once and the
///    library is never destroyed — letting process exit reclaim it is the only safe option.
///
/// 2. **PDFium is not thread-safe.** Standard builds have no internal locking, and concurrent
///    calls — even on different documents — corrupt shared state and take the process down
///    with an access violation, not an exception. The agent renders from several job threads
///    and the test host runs classes in parallel, so every entry point is serialised here.
///
/// A single global lock does mean one page renders at a time. That is the right trade for a
/// workstation agent: a page is tens of milliseconds, jobs are a handful of pages, and the
/// alternative is a crash that loses the whole spool.
/// </remarks>
internal static class PdfiumRuntime
{
    private static readonly Lock InitGate = new();

    /// <summary>Held across every PDFium call. See the remarks above before removing it.</summary>
    internal static readonly Lock ApiGate = new();

    private static bool initialised;

    public static void EnsureInitialised()
    {
        if (initialised)
        {
            return;
        }

        lock (InitGate)
        {
            if (initialised)
            {
                return;
            }

            lock (ApiGate)
            {
                Pdfium.InitLibrary();
            }

            initialised = true;
        }
    }

    /// <summary>Runs <paramref name="action"/> with exclusive access to PDFium.</summary>
    public static T Locked<T>(Func<T> action)
    {
        EnsureInitialised();
        lock (ApiGate)
        {
            return action();
        }
    }

    /// <summary>Runs <paramref name="action"/> with exclusive access to PDFium.</summary>
    public static void Locked(Action action)
    {
        EnsureInitialised();
        lock (ApiGate)
        {
            action();
        }
    }
}

/// <summary>A loaded PDF, pinned for as long as PDFium holds a pointer into it.</summary>
public sealed class PdfDocument : IDisposable
{
    private readonly GCHandle pinned;

    private IntPtr handle;

    private PdfDocument(IntPtr handle, GCHandle pinned)
    {
        this.handle = handle;
        this.pinned = pinned;
    }

    public int PageCount => PdfiumRuntime.Locked(() => Pdfium.GetPageCount(handle));

    internal IntPtr Handle => handle;

    /// <summary>Loads a PDF from memory. The buffer is pinned until the document is disposed.</summary>
    public static PdfDocument Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("PDF buffer is empty", nameof(bytes));
        }

        PdfiumRuntime.EnsureInitialised();

        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var (document, error) = PdfiumRuntime.Locked(() =>
        {
            var loaded = Pdfium.LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
            return (loaded, loaded == IntPtr.Zero ? Pdfium.GetLastError() : 0u);
        });

        if (document == IntPtr.Zero)
        {
            pinned.Free();
            throw new InvalidOperationException($"PDFium could not load the document (error {error})");
        }

        return new PdfDocument(document, pinned);
    }

    public PdfPage OpenPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex), pageIndex, $"document has {PageCount} page(s)");
        }

        var page = PdfiumRuntime.Locked(() => Pdfium.LoadPage(handle, pageIndex));
        if (page == IntPtr.Zero)
        {
            throw new InvalidOperationException($"PDFium could not load page {pageIndex + 1}");
        }

        return new PdfPage(page);
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            var closing = handle;
            PdfiumRuntime.Locked(() => Pdfium.CloseDocument(closing));
            handle = IntPtr.Zero;
        }

        if (pinned.IsAllocated)
        {
            pinned.Free();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>One page of a <see cref="PdfDocument"/>.</summary>
public sealed class PdfPage : IDisposable
{
    private IntPtr handle;

    internal PdfPage(IntPtr handle) => this.handle = handle;

    /// <summary>Page width in PDF points, accounting for the declared rotation.</summary>
    public double WidthPoints => PdfiumRuntime.Locked(() => (double)Pdfium.GetPageWidth(handle));

    /// <summary>Page height in PDF points, accounting for the declared rotation.</summary>
    public double HeightPoints => PdfiumRuntime.Locked(() => (double)Pdfium.GetPageHeight(handle));

    public double WidthMm => WidthPoints / 72.0 * 25.4;

    public double HeightMm => HeightPoints / 72.0 * 25.4;

    /// <summary>Declared page rotation, in degrees (0/90/180/270).</summary>
    public int Rotation => PdfiumRuntime.Locked(() => Pdfium.GetPageRotation(handle)) * 90;

    internal IntPtr Handle => handle;

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            var closing = handle;
            PdfiumRuntime.Locked(() => Pdfium.ClosePage(closing));
            handle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
