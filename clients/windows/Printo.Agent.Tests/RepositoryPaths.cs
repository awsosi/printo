namespace Printo.Agent.Tests;

/// <summary>
/// Locates repository-level test data from wherever the test assembly happens to run.
/// </summary>
/// <remarks>
/// The conformance fixtures and the extracted corpus are shared with the TypeScript engine and
/// therefore live at the repository root, not inside the C# tree. Walking up from the assembly
/// keeps that working under <c>dotnet test</c>, under an IDE runner and inside a container,
/// without a hard-coded relative depth that breaks whenever the output path changes.
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly Lazy<string?> RootValue = new(FindRoot);

    /// <summary>Repository root, or <c>null</c> when the tests run outside a checkout.</summary>
    public static string? Root => RootValue.Value;

    public static string? Conformance => Combine("tests", "conformance");

    public static string? CorpusFeatures => Combine("tests", "corpus", "features.jsonl.gz");

    public static string? CorpusExpected => Combine("tests", "corpus", "expected.json");

    public static string? Profiles => Combine("profiles");

    /// <summary>
    /// The sample PDFs, which live outside the repository because they are customer data.
    /// Overridable with PRINTO_CORPUS_DIR; otherwise looked for beside the checkout.
    /// </summary>
    public static string? CorpusPdfs
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PRINTO_CORPUS_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Directory.Exists(configured) ? configured : null;
            }

            if (Root is null)
            {
                return null;
            }

            var sibling = Path.Combine(Directory.GetParent(Root)?.FullName ?? Root, "printo-materials");
            return Directory.Exists(sibling) ? sibling : null;
        }
    }

    private static string? Combine(params string[] parts)
    {
        if (Root is null)
        {
            return null;
        }

        var path = Path.Combine([Root, .. parts]);
        return Directory.Exists(path) || File.Exists(path) ? path : null;
    }

    private static string? FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // `profiles/` plus `packages/routing-engine` identifies this repository
            // unambiguously; a lone `.git` would also match a parent checkout.
            if (Directory.Exists(Path.Combine(directory.FullName, "profiles"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages", "routing-engine")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
