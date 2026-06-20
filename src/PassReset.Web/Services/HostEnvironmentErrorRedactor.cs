using Microsoft.Extensions.Hosting;
using PassReset.Common;
using PassReset.Common.ChangeFlow;

namespace PassReset.Web.Services;

/// <summary>
/// STAB-013 adapter: applies the enumeration-code collapse only in the Production
/// environment, matching the behavior previously inlined in PasswordController.
/// </summary>
public sealed class HostEnvironmentErrorRedactor(IHostEnvironment environment) : IErrorRedactor
{
    private readonly IHostEnvironment _environment = environment;

    public ApiErrorItem Redact(ApiErrorItem error) =>
        _environment.IsProduction() && IErrorRedactor.IsAccountEnumerationCode(error.ErrorCode)
            ? new ApiErrorItem(ApiErrorCode.Generic, error.Message)
            : error;
}
