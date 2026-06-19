using PassReset.Common;

namespace PassReset.Web.Helpers;

/// <summary>
/// No-op password change provider used in development.
/// Selected at runtime when WebSettings.UseDebugProvider is true.
/// Returns deterministic errors based on well-known test usernames,
/// allowing UI flows to be exercised without an Active Directory connection.
/// </summary>
internal sealed class DebugPasswordChangeProvider : IPasswordChanger, IPasswordStatusReader, IDirectoryUserReader
{
    private readonly ILogger<DebugPasswordChangeProvider> _logger;

    public DebugPasswordChangeProvider(ILogger<DebugPasswordChangeProvider> logger)
    {
        _logger = logger;
    }

    public Task<ApiErrorItem?> PerformPasswordChangeAsync(string username, string currentPassword, string newPassword)
    {
        var localPart = username.Contains('@')
            ? username[..username.IndexOf('@')]
            : username;

        _logger.LogDebug("DebugPasswordChangeProvider: PerformPasswordChange called for user={User}", localPart);

        ApiErrorItem? result = localPart switch
        {
            "error"             => new ApiErrorItem(ApiErrorCode.Generic, "Simulated generic error"),
            "changeNotPermitted"=> new ApiErrorItem(ApiErrorCode.ChangeNotPermitted),
            "fieldMismatch"     => new ApiErrorItem(ApiErrorCode.FieldMismatch),
            "fieldRequired"     => new ApiErrorItem(ApiErrorCode.FieldRequired),
            "invalidCaptcha"    => new ApiErrorItem(ApiErrorCode.InvalidCaptcha),
            "invalidCredentials"=> new ApiErrorItem(ApiErrorCode.InvalidCredentials),
            "invalidDomain"     => new ApiErrorItem(ApiErrorCode.InvalidDomain),
            "userNotFound"      => new ApiErrorItem(ApiErrorCode.UserNotFound),
            "ldapProblem"       => new ApiErrorItem(ApiErrorCode.LdapProblem),
            "pwnedPassword"     => new ApiErrorItem(ApiErrorCode.PwnedPassword),
            "passwordTooYoung"  => new ApiErrorItem(ApiErrorCode.PasswordTooYoung),
            _                   => null   // success
        };
        return Task.FromResult(result);
    }

    public string? GetUserEmail(string username) =>
        $"{username.Split('@')[0]}@debug.local";

    public IEnumerable<(string Username, string Email, DateTime? PasswordLastSet)> GetUsersInGroup(string groupName)
    {
        _logger.LogDebug("DebugPasswordChangeProvider: GetUsersInGroup called (returning empty)");
        return [];
    }

    public TimeSpan GetDomainMaxPasswordAge() => TimeSpan.FromDays(90);

    public Task<PasswordStatus> GetUserPasswordStatusAsync(string username, string currentPassword)
    {
        // Magic usernames mirror the existing debug change-flow stubs.
        var status = username switch
        {
            "invalidCredentials" => new PasswordStatus(false, ApiErrorCode.InvalidCredentials, null, false, ExpirySource.Unknown, null),
            "userNotFound"       => new PasswordStatus(false, ApiErrorCode.UserNotFound,       null, false, ExpirySource.Unknown, null),
            "neverExpires"       => new PasswordStatus(true,  null, null, true,  ExpirySource.Resolved, DebugPolicy()),
            _                    => new PasswordStatus(true,  null, DateTimeOffset.UtcNow.AddDays(12), false, ExpirySource.Resolved, DebugPolicy()),
        };
        return Task.FromResult(status);
    }

    private static PasswordPolicy DebugPolicy() => new(
        MinLength: 12, RequiresComplexity: true, HistoryLength: 24, MinAgeDays: 1, MaxAgeDays: 90);

    public Task<PasswordPolicy?> GetEffectivePasswordPolicyAsync() =>
        Task.FromResult<PasswordPolicy?>(new PasswordPolicy(12, true, 24, 1, 90));
}
