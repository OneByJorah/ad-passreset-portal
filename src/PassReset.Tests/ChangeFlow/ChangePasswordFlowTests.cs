using NSubstitute;
using PassReset.Common;
using PassReset.Common.ChangeFlow;
using Xunit;

namespace PassReset.Tests.ChangeFlow;

public class ChangePasswordFlowTests
{
    private sealed class TestSettings : IChangeFlowSettings
    {
        public int MinimumDistance { get; init; }
        public bool RecaptchaEnabled { get; init; }
        public string RecaptchaAction { get; init; } = "change_password";
        public bool NotificationEnabled { get; init; }
    }

    // Passthrough redactor: proves the flow calls the seam without applying env rules here.
    private sealed class PassthroughRedactor : IErrorRedactor
    {
        public int Calls { get; private set; }
        public ApiErrorItem Redact(ApiErrorItem error) { Calls++; return error; }
    }

    private static ChangePasswordRequest Req(string user = "alice", string cur = "OldPass1!", string @new = "BrandNewP@ss123") =>
        new(user, cur, @new) { Recaptcha = "tok" };

    private static RequestContext Ctx() => new(ClientIp: "10.0.0.1", TraceId: "trace-123");

    private static (ChangePasswordFlow flow, IPasswordChanger changer, IRecaptchaVerifier recaptcha,
        ISiemService siem, PassthroughRedactor redactor) Build(IChangeFlowSettings settings)
    {
        var changer = Substitute.For<IPasswordChanger>();
        var recaptcha = Substitute.For<IRecaptchaVerifier>();
        var siem = Substitute.For<ISiemService>();
        var redactor = new PassthroughRedactor();
        var flow = new ChangePasswordFlow(changer, recaptcha, siem, redactor, settings);
        return (flow, changer, recaptcha, siem, redactor);
    }

    [Fact]
    public async Task HappyPath_ReturnsOk_AndEmitsPasswordChanged()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync("alice", "OldPass1!", "BrandNewP@ss123")
            .Returns((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.Ok, outcome.Disposition);
        Assert.Equal("Password changed successfully.", outcome.SuccessMessage);
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.PasswordChanged));
    }

    [Fact]
    public async Task HappyPath_WhenNotificationEnabled_ReturnsNotificationRequest()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { NotificationEnabled = true });
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(user: "bob"), Ctx());

        Assert.NotNull(outcome.Notification);
        Assert.Equal("bob", outcome.Notification!.Username);
        Assert.Equal("10.0.0.1", outcome.Notification.ClientIp);
    }

    [Fact]
    public async Task HappyPath_WhenNotificationDisabled_NoNotificationRequest()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { NotificationEnabled = false });
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Null(outcome.Notification);
    }

    [Fact]
    public async Task DistanceTooLow_ReturnsValidation_WithoutCallingChanger()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { MinimumDistance = 50 });

        var outcome = await flow.HandleAsync(Req(cur: "samePassword1!", @new: "samePassword2!"), Ctx());

        Assert.Equal(Disposition.ValidationError, outcome.Disposition);
        Assert.Equal(ApiErrorCode.MinimumDistance, outcome.Error!.ErrorCode);
        await changer.DidNotReceiveWithAnyArgs().PerformPasswordChangeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task RecaptchaFails_ReturnsCaptchaRejected_WithoutCallingChanger()
    {
        var (flow, changer, recaptcha, siem, _) = Build(new TestSettings { RecaptchaEnabled = true });
        recaptcha.VerifyAsync("tok", "change_password", "10.0.0.1").Returns(false);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.CaptchaRejected, outcome.Disposition);
        Assert.Equal(ApiErrorCode.InvalidCaptcha, outcome.Error!.ErrorCode);
        await changer.DidNotReceiveWithAnyArgs().PerformPasswordChangeAsync(default!, default!, default!);
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.RecaptchaFailed));
    }

    [Fact]
    public async Task ChangeFails_ReturnsChangeFailed_AndRedactsThroughSeam()
    {
        var (flow, changer, _, siem, redactor) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!)
            .ReturnsForAnyArgs(new ApiErrorItem(ApiErrorCode.InvalidCredentials));

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.ChangeFailed, outcome.Disposition);
        Assert.Equal(1, redactor.Calls); // proves redaction goes through the seam, not inline
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.InvalidCredentials));
    }

    [Fact]
    public async Task ChangeFails_AuditCarriesTraceIdFromContext()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!)
            .ReturnsForAnyArgs(new ApiErrorItem(ApiErrorCode.UserNotFound));

        await flow.HandleAsync(Req(), Ctx());

        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.TraceId == "trace-123" && e.ClientIp == "10.0.0.1"));
    }

    [Fact]
    public async Task EntryAlwaysEmitsAttemptStarted()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        await flow.HandleAsync(Req(), Ctx());

        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.PasswordChangeAttemptStarted));
    }
}
