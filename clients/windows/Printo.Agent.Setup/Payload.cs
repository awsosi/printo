using System.IO.Compression;
using System.Reflection;

namespace Printo.Agent.Setup;

/// <summary>
/// The product this installer carries, and laying it down on disk.
/// </summary>
/// <remarks>
/// <para>
/// The same files the MSI installs, from the same publish, zipped by <c>build.ps1</c> and
/// embedded in this executable. Self-contained, so a workstation needs no .NET installed first:
/// a fleet is far easier to keep correct when the installer carries its own runtime than when
/// every machine needs a matching version deployed ahead of it, and one missing prerequisite is
/// a packing bench that cannot print.
/// </para>
/// <para>
/// Absent when the project is built on its own, which is how the tests build it. The program
/// says so and refuses rather than reporting a successful install of nothing.
/// </para>
/// </remarks>
internal static class Payload
{
    /// <summary>The resource name <c>build.ps1</c> embeds the zip under.</summary>
    public const string ResourceName = "Printo.Agent.Payload.zip";

    public static bool IsPresent =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>
    /// Extracts everything into <paramref name="directory"/>, and says what it wrote.
    /// </summary>
    /// <returns>Every file written, relative to the directory, in the order they were written.</returns>
    /// <remarks>
    /// The list is the point. Windows Installer keeps its own record of what a package put where,
    /// and an installer without one has to choose between leaving the previous version's orphans
    /// behind on upgrade and deleting the whole directory - which takes with it anything an
    /// administrator put there, a licence file or a support tool or a copy of the log. Recording
    /// what was written does neither.
    /// </remarks>
    public static IReadOnlyList<string> Extract(string directory)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "this installer was built without its payload, so there is nothing to install. " +
                "Build it with clients/windows/installer/build.ps1.");

        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);

        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        var written = new List<string>();

        foreach (var entry in archive.Entries)
        {
            // A directory entry, which the zip records with a trailing separator and no content.
            if (entry.Name.Length == 0) { continue; }

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // The archive is one this build produced, so this cannot currently trip - but an
            // extractor that will follow `..\..\windows\system32` out of its own directory is a
            // bad thing to have in a program that runs as an administrator, whatever it is being
            // fed today.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "the payload contains an entry that would be written outside the install " +
                    "directory: " + entry.FullName);
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }

            // An existing file may be read-only, which is how a previous install's files come
            // back off some backup products.
            if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); }

            entry.ExtractToFile(target, overwrite: true);
            written.Add(entry.FullName.Replace('/', Path.DirectorySeparatorChar));
        }

        return written;
    }

    /// <summary>Roughly what the installed product will occupy, in kilobytes.</summary>
    /// <remarks>Add/Remove Programs shows this, and a blank there reads as a broken entry.</remarks>
    public static int InstalledKilobytes()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null) { return 0; }

        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        var bytes = archive.Entries.Sum(entry => entry.Length);
        return (int)Math.Max(1, bytes / 1024);
    }
}
