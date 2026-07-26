namespace MecchaCamouflage.Core;

/// <summary>Build-time capability gates. Production releases must not gain research features from an environment variable alone.</summary>
public static class BuildFeatures
{
#if MECCHA_RESEARCH_BUILD
    public const bool IsResearchBuild = true;
#else
    public const bool IsResearchBuild = false;
#endif

    public static bool ResearchArtifactsEnabled =>
        IsResearchBuild &&
        string.Equals(Environment.GetEnvironmentVariable("MECCHA_RESEARCH_ARTIFACTS"), "1", StringComparison.Ordinal);
}
