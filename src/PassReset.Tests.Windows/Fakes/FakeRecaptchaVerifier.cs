using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Fakes;

/// <summary>
/// In-memory <see cref="IRecaptchaVerifier"/> for controller tests. Returns a fixed result
/// and records every call so tests can assert on the forwarded action/IP.
/// </summary>
public sealed class FakeRecaptchaVerifier : IRecaptchaVerifier
{
    private readonly bool _result;
    public List<(string Token, string Action, string ClientIp)> Calls { get; } = new();

    public FakeRecaptchaVerifier(bool result) => _result = result;

    public Task<bool> VerifyAsync(string token, string action, string clientIp)
    {
        Calls.Add((token, action, clientIp));
        return Task.FromResult(_result);
    }
}
