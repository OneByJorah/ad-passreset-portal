namespace PassReset.Web.Models;

/// <summary>
/// Operator-managed toggles for the GET /api/health probes. Each connectivity
/// probe can be disabled independently so a host on a restricted network can keep
/// the health endpoint green for the dependencies it CAN reach. Disabling a probe
/// reports its status as "skipped" and excludes it from the aggregate rollup.
/// </summary>
public class HealthCheckSettings
{
    /// <summary>When true, the SMTP TCP connectivity probe is not run (status "skipped").</summary>
    public bool DisableSmtpConnectivityProbe { get; set; }

    /// <summary>When true, the password-expiry background-service check is not run (status "skipped").</summary>
    public bool DisableExpiryServiceCheck { get; set; }

    /// <summary>When true, the Active Directory connectivity probe is not run (status "skipped").</summary>
    public bool DisableAdConnectivityProbe { get; set; }

    /// <summary>
    /// Grace period, in seconds, after process start during which an enabled-but-not-yet-run
    /// expiry service is reported "healthy" rather than "degraded". A not-yet-run service on a
    /// fresh deploy is startup lag, not misconfiguration. After this window with still no tick,
    /// the service reverts to "degraded" so a genuinely stuck service is surfaced. Default 600 (10 min).
    /// </summary>
    public int ExpiryServiceGracePeriodSeconds { get; set; } = 600;
}
