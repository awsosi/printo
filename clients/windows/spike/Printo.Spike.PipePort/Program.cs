using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

// ---------------------------------------------------------------------------------------------
// M1 Tier 2 capture spike (throwaway).
//
// The Windows Local Port monitor opens its port name with CreateFile, so a named pipe can be
// used as a printer port. This process hosts that pipe and writes whatever the spooler pushes
// into it, which answers the second open M1 question: can an inbox "Microsoft Print To PDF"
// instance be bound to a port we own (rather than PORTPROMPT:) and does it emit a clean PDF
// per job?
//
// The spooler runs as LocalSystem, so the pipe is created with an explicit ACL granting
// SYSTEM and Administrators access - the .NET default would only admit the creating user.
// ---------------------------------------------------------------------------------------------

var pipeName = "printo-spike-port";
var outputRoot = Path.Combine(AppContext.BaseDirectory, "capture");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length:
            pipeName = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outputRoot = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

Directory.CreateDirectory(outputRoot);

var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
    PipeAccessRights.FullControl,
    AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    PipeAccessRights.FullControl,
    AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User!,
    PipeAccessRights.FullControl,
    AccessControlType.Allow));

Console.WriteLine($"Printo pipe-port capture spike on \\\\.\\pipe\\{pipeName}");
Console.WriteLine($"  capture dir : {outputRoot}");
Console.WriteLine("Press Ctrl+C to stop.");

var sequence = 0;

while (true)
{
    using var server = NamedPipeServerStreamAcl.Create(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.None,
        inBufferSize: 64 * 1024,
        outBufferSize: 64 * 1024,
        pipeSecurity: security);

    await server.WaitForConnectionAsync();
    sequence++;

    using var buffer = new MemoryStream();
    await server.CopyToAsync(buffer);
    var data = buffer.ToArray();

    var (format, extension) = Sniff(data);
    var path = Path.Combine(
        outputRoot,
        $"pipe-{sequence:D4}-{DateTime.Now:HHmmss}.{extension}");
    await File.WriteAllBytesAsync(path, data);

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"  connection {sequence}: {data.Length} bytes, {format} -> {Path.GetFileName(path)}"));

    server.Disconnect();
}

static (string Format, string Extension) Sniff(byte[] data)
{
    if (data.Length >= 4)
    {
        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic == "%PDF")
        {
            return ("application/pdf", "pdf");
        }

        if (magic is "RaS2" or "2SaR")
        {
            return ("image/pwg-raster", "pwg");
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
