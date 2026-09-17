namespace Printo.Agent.Setup;

/// <summary>
/// What the installer did, said twice: to whoever is watching, and to a file.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the MSI that mattered most and was hardest to get. Windows Installer runs
/// behind a progress window that appears and vanishes, so a fatal rollback and a clean install
/// look identical from the outside - which is exactly how a 1603 on the first machine this was
/// ever installed on came back as "it flashes and disappears", and why the package had to be
/// given a user interface.
/// </para>
/// <para>
/// A console program does not have that problem if it says what it is doing, step by step, and
/// stays on screen at the end. So there is no progress bar here and no dialog: there is a
/// transcript, and it is the same transcript in the window and in the log file, so a person
/// reading a support mail is reading what the operator saw.
/// </para>
/// </remarks>
internal sealed class Transcript : IDisposable
{
    private readonly StreamWriter? file;

    private Transcript(string? path, StreamWriter? file)
    {
        Path = path;
        this.file = file;
    }

    /// <summary>Where the transcript is being kept, if it is.</summary>
    public string? Path { get; }

    /// <summary>True once anything has been reported as a failure.</summary>
    public bool Failed { get; private set; }

    public static Transcript Open(string? path)
    {
        path ??= System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "printo-setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

            var file = new StreamWriter(path, append: true) { AutoFlush = true };
            return new Transcript(path, file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be opened is not a reason to refuse to install. The window still
            // shows everything; only the copy is lost, and it says so.
            Console.WriteLine("  note   the transcript could not be written to " + path + ": " + error.Message);
            return new Transcript(null, null);
        }
    }

    /// <summary>A step about to be attempted.</summary>
    public void Step(string what) => Write("==>", what, ConsoleColor.Cyan);

    /// <summary>Something that worked.</summary>
    public void Done(string what) => Write("  ok", what, ConsoleColor.Green);

    /// <summary>Something worth knowing that is not a verdict either way.</summary>
    public void Note(string what) => Write("note", what, ConsoleColor.Gray);

    /// <summary>Something that did not work but that the install can carry on without.</summary>
    public void Warn(string what) => Write("warn", what, ConsoleColor.Yellow);

    /// <summary>Something that did not work and that the install cannot carry on without.</summary>
    public void Fail(string what)
    {
        Failed = true;
        Write("FAIL", what, ConsoleColor.Red);
    }

    private void Write(string tag, string text, ConsoleColor colour)
    {
        var line = tag + "  " + text;

        // Colour is a console property, and this may well be running with no console: from a
        // machine startup script, or from a management agent. Losing the colour there is nothing;
        // throwing on it would fail the install over the way a line looks.
        try
        {
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = colour;
                Console.WriteLine(line);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
        catch (IOException)
        {
            Console.WriteLine(line);
        }

        // The file gets a clock as well: an install that took four minutes because a printer
        // enumeration hung reads very differently from one that took four seconds, and the
        // difference is invisible without timestamps.
        file?.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
    }

    public void Dispose() => file?.Dispose();
}
