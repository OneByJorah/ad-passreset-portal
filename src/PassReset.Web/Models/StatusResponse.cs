using PassReset.Common;

namespace PassReset.Web.Models;

/// <summary>Success wire DTO for a Status Check. Carries no enumeration-sensitive data.</summary>
public sealed record StatusResponse(
    bool Authenticated,
    string? ExpiresUtc,
    bool NeverExpires,
    string Source,
    PasswordPolicy? Policy);
