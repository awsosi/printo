namespace Printo.Agent.Setup;

/// <summary>
/// What this program returns, and what a deployment script should read.
/// </summary>
/// <remarks>
/// Deliberately few and deliberately not Windows Installer's. Borrowing 1603 would invite
/// somebody to look it up and read about an installer that was never involved; these are small
/// numbers with one meaning each, documented in DEPLOYMENT.md beside the command that produces
/// them.
/// </remarks>
internal static class ExitCodes
{
    /// <summary>It did what was asked.</summary>
    public const int Ok = 0;

    /// <summary>A step that the outcome depends on did not work. The transcript says which.</summary>
    public const int Failed = 1;

    /// <summary>The command line could not be read.</summary>
    public const int BadUsage = 2;

    /// <summary>It needs an administrator, and did not have one.</summary>
    public const int NeedsElevation = 5;

    /// <summary>This machine cannot run the product at all.</summary>
    public const int Unsupported = 6;
}
