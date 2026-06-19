using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PassReset.Web.Models;
using PassReset.Web.Services;
using PassReset.Tests.Windows.Fakes;
using Xunit;

namespace PassReset.Tests.Windows.Web.Services;

public class GoogleRecaptchaVerifierTests
{
    private static GoogleRecaptchaVerifier Build(
        FakeHttpMessageHandler handler, float threshold = 0.5f, bool failOpen = false)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.google.com/") };
        var settings = Options.Create(new ClientSettings
        {
            Recaptcha = new Recaptcha
            {
                Enabled = true,
                PrivateKey = "test-private-key",
                ScoreThreshold = threshold,
                FailOpenOnUnavailable = failOpen,
            },
        });
        return new GoogleRecaptchaVerifier(http, settings, NullLogger<GoogleRecaptchaVerifier>.Instance);
    }

    private static FakeHttpMessageHandler Json(string body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task ValidHumanToken_MatchingAction_ReturnsTrue()
    {
        var v = Build(Json("""{"success":true,"score":0.9,"action":"change_password"}"""));
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task ScoreBelowThreshold_ReturnsFalse()
    {
        var v = Build(Json("""{"success":true,"score":0.3,"action":"change_password"}"""), threshold: 0.5f);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task WrongAction_ReturnsFalse()
    {
        var v = Build(Json("""{"success":true,"score":0.9,"action":"login"}"""));
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task SuccessFalse_ReturnsFalse()
    {
        var v = Build(Json("""{"success":false,"score":0.9,"action":"change_password"}"""));
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Non2xx_FailOpenTrue_ReturnsTrue()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        var v = Build(handler, failOpen: true);
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Non2xx_FailOpenFalse_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        var v = Build(handler, failOpen: false);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task NetworkThrow_FailOpenTrue_ReturnsTrue()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));
        var v = Build(handler, failOpen: true);
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task ParseError_NeverFailOpen_ReturnsFalse()
    {
        // 200 OK but unparseable body → unexpected error path, must NOT fail open even when failOpen=true.
        var v = Build(Json("not-json"), failOpen: true);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task NetworkThrow_FailOpenFalse_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));
        var v = Build(handler, failOpen: false);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Timeout_FailOpenTrue_ReturnsTrue()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        var v = Build(handler, failOpen: true);
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Timeout_FailOpenFalse_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        var v = Build(handler, failOpen: false);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task NullConfig_ReturnsFalse()
    {
        // Verifier with no Recaptcha config at all — should fail closed, never throw.
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should not be called"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.google.com/") };
        var settings = Options.Create(new ClientSettings()); // Recaptcha left null
        var verifier = new GoogleRecaptchaVerifier(http, settings, NullLogger<GoogleRecaptchaVerifier>.Instance);
        Assert.False(await verifier.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }
}
