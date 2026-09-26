namespace PaciolanEvenue.Fetch;

/// <summary>
/// Tuning for <see cref="PaciolanEvenueClient"/>. Defaults mirror python/paciolanevenue/client.py's
/// PaciolanEvenueClient.__init__ so behaviour matches the Python fetcher out of the box - including
/// the higher retry count than Broadway's default (3): PerimeterX was NOT reliably passed in one
/// attempt during probing (see python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md), so retrying with a fresh proxy
/// {SESSIONID} up to <see cref="Retries"/> times is the actual mitigation, not a bigger single-try
/// timeout.
/// </summary>
public sealed class PaciolanEvenueFetchOptions
{
    /// <summary>Number of DIFFERENT proxy sessions (egress IPs) to try before giving up on one
    /// event.</summary>
    public int Retries { get; init; } = 4;

    public double SleepSeconds { get; init; } = 0.5;

    public int TimeoutSeconds { get; init; } = 20;

    /// <summary>How long PaciolanEvenueBrowser polls for a PerimeterX block marker to clear before
    /// giving up on one attempt.</summary>
    public double SettleMaxSeconds { get; init; } = 15.0;
}
