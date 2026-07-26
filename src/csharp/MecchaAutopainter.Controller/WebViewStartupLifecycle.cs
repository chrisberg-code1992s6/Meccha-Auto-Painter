namespace MecchaCamouflage.Controller;

/// <summary>
/// Tracks one WebView instance from navigation through the page's uiReady message.
/// A caller can use the one-time <c>true</c> result to schedule work that must run
/// only after both parts of startup have completed.
/// </summary>
public sealed class WebViewStartupLifecycle
{
    private long generation;
    private ulong? initialNavigationId;
    private bool navigationSucceeded;
    private bool uiReady;
    private bool stabilizationRequested;

    public long Begin()
    {
        generation = checked(generation + 1);
        initialNavigationId = null;
        navigationSucceeded = false;
        uiReady = false;
        stabilizationRequested = false;
        return generation;
    }

    public bool IsCurrent(long candidate) => candidate == generation;

    public bool RegisterInitialNavigation(long candidate, ulong navigationId)
    {
        if (!IsCurrent(candidate))
            return false;
        if (initialNavigationId is null)
        {
            initialNavigationId = navigationId;
            return true;
        }
        return initialNavigationId == navigationId;
    }

    public bool IsInitialNavigation(long candidate, ulong navigationId) =>
        IsCurrent(candidate) && initialNavigationId == navigationId;

    public bool MarkNavigationSucceeded(long candidate, ulong navigationId)
    {
        if (!IsInitialNavigation(candidate, navigationId))
            return false;
        navigationSucceeded = true;
        return TryRequestStabilization();
    }

    public bool MarkUiReady(long candidate)
    {
        if (!IsCurrent(candidate))
            return false;
        uiReady = true;
        return TryRequestStabilization();
    }

    private bool TryRequestStabilization()
    {
        if (stabilizationRequested || !navigationSucceeded || !uiReady)
            return false;
        stabilizationRequested = true;
        return true;
    }
}
