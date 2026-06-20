namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Change Flow: the full server-side sequence above the Password Changer seam.
/// Owns minimum-distance validation, the reCAPTCHA gate, the credentialed change,
/// Error Redaction, and the SIEM audit emitted at each decision point. Returns a
/// fully-resolved <see cref="ChangePasswordOutcome"/>; performs no HTTP and no email I/O.
/// </summary>
public interface IChangePasswordFlow
{
    Task<ChangePasswordOutcome> HandleAsync(ChangePasswordRequest request, RequestContext context);
}
