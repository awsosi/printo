using System.Globalization;
using System.Text;

namespace Printo.Agent.Runtime;

/// <summary>
/// Writes log lines to a file that is rotated by size, keeping only the newest few.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the service and the tray, each writing its own file in the same folder, so a
/// support call can ask for one directory and get both sides of a conversation between them.
/// </para>
/// <para>
/// Settings are read on every write rather than captured, so turning logging on, changing the
/// level or the rotation - from the settings window or by a fleet policy arriving from the
/// server - takes effect on the next line without a restart. Off costs one delegate call per
/// line and nothing on disk.
/// </para>
/// <para>
/// Never throws. A log that cannot be written must not take down the process that is trying to
/// explain itself; the first failure is remembered for the status window and the rest are
/// dropped until the folder becomes writable again.
/// </para>
/// </remarks>
public sealed class RollingFileLog : IDisposable
{
    private readonly Func<LoggingSettings> settings;

    private readonly Func<string> directory;

    private readonly string baseName;

    private readonly Lock gate = new();

    private FileStream? stream;

    private StreamWriter? writer;

    private string? openPath;

    /// <param name="settings">Read on every write.</param>
    /// <param name="directory">Where the files go; read on every write as well.</param>
    /// <param name="baseName">File name without extension, e.g. <c>agent</c> or <c>tray-jsmith</c>.</param>
    public RollingFileLog(Func<LoggingSettings> settings, Func<string> directory, string baseName)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        this.baseName = string.Concat(baseName.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>Wall clock, overridable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Why the last write failed, or <c>null</c> when writing works.</summary>
    public string? LastError { get; private set; }

    /// <summary>The file currently being written, when logging is on.</summary>
    public string? CurrentPath => openPath;

    /// <summary>True when a line at <paramref name="level"/> would be written.</summary>
    public bool IsEnabled(AgentLogLevel level)
    {
        var current = settings();
        return current.FileEnabled && level >= current.Level;
    }

    /// <summary>Writes one line, if logging is on and the level is high enough.</summary>
    public void Write(AgentLogLevel level, string category, string message, Exception? error = null)
    {
        var current = settings().Normalised();
        if (!current.FileEnabled)
        {
            CloseIfOpen();
            return;
        }

        if (level < current.Level)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(Clock().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category)
            .Append(": ")
            .Append(message);

        if (error is not null)
        {
            line.AppendLine().Append(error);
        }

        lock (gate)
        {
            try
            {
                var path = Path.Combine(directory(), baseName + ".log");
                EnsureOpen(path);
                writer!.WriteLine(line.ToString());
                writer.Flush();
                LastError = null;

                if (stream!.Length >= (long)current.MaxFileSizeMb * 1024 * 1024)
                {
                    Rotate(path, current.MaxFiles);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                LastError = failure.Message;
                CloseLocked();
            }
        }
    }

    /// <summary>All files this log has produced, newest first.</summary>
    public IReadOnlyList<string> Files()
    {
        var folder = directory();
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return new DirectoryInfo(folder)
            .EnumerateFiles(baseName + "*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .ToList();
    }

    public void Dispose()
    {
        lock (gate)
        {
            CloseLocked();
        }
    }

    private void EnsureOpen(string path)
    {
        if (writer is not null && string.Equals(openPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CloseLocked();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Shared for reading and deleting, so somebody can open the live file in Notepad, and the
        // tray can clear the folder, without either stopping the service writing it.
        stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        openPath = path;
    }

    /// <summary>
    /// Closes the current file under a timestamped name and deletes all but the newest
    /// <paramref name="maxFiles"/>, the new current file included.
    /// </summary>
    private void Rotate(string path, int maxFiles)
    {
        CloseLocked();

        var stamp = Clock().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var archived = Path.Combine(Path.GetDirectoryName(path)!, $"{baseName}-{stamp}.log");
        File.Move(path, archived, overwrite: true);

        var folder = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var rotated = folder
            .EnumerateFiles(baseName + "-*.log")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();

        // The current file counts towards the limit, so keep one fewer archive than the maximum.
        foreach (var stale in rotated.Skip(Math.Max(0, maxFiles - 1)))
        {
            try
            {
                stale.Delete();
            }
            catch (IOException)
            {
                // Open in an editor; it goes at the next rotation.
            }
        }
    }

    private void CloseIfOpen()
    {
        if (writer is null)
        {
            return;
        }

        lock (gate)
        {
            CloseLocked();
        }
    }

    private void CloseLocked()
    {
        writer?.Dispose();
        stream?.Dispose();
        writer = null;
        stream = null;
        openPath = null;
    }

    private static string Abbreviate(AgentLogLevel level) => level switch
    {
        AgentLogLevel.Debug => "DBG",
        AgentLogLevel.Information => "INF",
        AgentLogLevel.Warning => "WRN",
        AgentLogLevel.Error => "ERR",
        _ => "CRT",
    };
}
