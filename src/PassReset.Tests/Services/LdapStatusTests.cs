using System.DirectoryServices.Protocols;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PassReset.Common;
using PassReset.PasswordProvider.Ldap;
using PassReset.Tests.Fakes;

namespace PassReset.Tests.Services;

public class LdapStatusTests
{
    [Fact]
    public void Decode_Zero_MeansNeverExpires()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("0");
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_Int64Max_MeansNeverExpires()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("9223372036854775807");
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_RealFileTime_ReturnsThatInstant()
    {
        // 2026-09-01T00:00:00Z as a Windows FILETIME.
        var ft = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime();
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry(ft.ToString());
        Assert.False(never);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), expires);
    }

    [Fact]
    public void Decode_Garbage_ReturnsNeitherExpiryNorNever()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("not-a-number");
        Assert.False(never);
        Assert.Null(expires);
    }

    // ----- GetUserPasswordStatusAsync: user-bind credential verification (Task 2b) -------------

    [Fact]
    public async Task GetUserPasswordStatusAsync_UserBindFails_ReturnsInvalidCredentials()
    {
        // Search resolves the user entry, but the read-only per-user bind fails ⇒ the method must
        // treat the supplied current password as wrong and short-circuit to InvalidCredentials.
        var (sut, fake) = Build();
        fake.UserBindResult = false;
        fake.OnSearch(
            "(sAMAccountName=alice)",
            MakeResponse(MakeEntry("CN=Alice,OU=Users,DC=corp,DC=example,DC=com",
                (LdapAttributeNames.PwdExpiryComputed, "0"))));

        var status = await sut.GetUserPasswordStatusAsync("alice", "WrongPass1!");

        Assert.False(status.Authenticated);
        Assert.Equal(ApiErrorCode.InvalidCredentials, status.Error);
        Assert.Equal(1, fake.TryBindAsUserCallCount);
        // The user-bind ran against the resolved DN with the supplied password.
        Assert.Equal(("CN=Alice,OU=Users,DC=corp,DC=example,DC=com", "WrongPass1!"), fake.LastUserBind);
    }

    [Fact]
    public async Task GetUserPasswordStatusAsync_UserBindSucceeds_ReturnsAuthenticatedWithExpiry()
    {
        // Search resolves the user entry carrying a real FILETIME expiry, and the per-user bind
        // succeeds ⇒ authenticated, Source=Resolved, ExpiresUtc decoded from the constructed attribute.
        var (sut, fake) = Build();
        fake.UserBindResult = true;
        var ft = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime();
        fake.OnSearch(
            "(sAMAccountName=alice)",
            MakeResponse(MakeEntry("CN=Alice,OU=Users,DC=corp,DC=example,DC=com",
                (LdapAttributeNames.PwdExpiryComputed, ft.ToString()))));

        var status = await sut.GetUserPasswordStatusAsync("alice", "RightPass1!");

        Assert.True(status.Authenticated);
        Assert.Null(status.Error);
        Assert.Equal(ExpirySource.Resolved, status.Source);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), status.ExpiresUtc);
        Assert.Equal(1, fake.TryBindAsUserCallCount);
    }

    [Fact]
    public async Task GetUserPasswordStatusAsync_UserNotFound_DoesNotAttemptUserBind()
    {
        // No entry resolves ⇒ UserNotFound (existing behavior); the user-bind must never run.
        var opts = new PasswordChangeOptions
        {
            AllowedUsernameAttributes = new[] { "samaccountname" },
            BaseDn = "DC=corp,DC=example,DC=com",
            ServiceAccountDn = "CN=svc,DC=corp,DC=example,DC=com",
            ServiceAccountPassword = "svcpw",
            LdapHostnames = new[] { "dc01.corp.example.com" },
            LdapPort = 636,
        };
        var (sut, fake) = Build(opts);
        fake.OnSearch("(sAMAccountName=ghost)", MakeResponse());

        var status = await sut.GetUserPasswordStatusAsync("ghost", "any");

        Assert.False(status.Authenticated);
        Assert.Equal(ApiErrorCode.UserNotFound, status.Error);
        Assert.Equal(0, fake.TryBindAsUserCallCount);
    }

    // ----- helpers (mirror LdapPasswordChangeProviderTests construction pattern) ---------------

    private static (LdapPasswordChangeProvider sut, FakeLdapSession fake) Build(
        PasswordChangeOptions? opts = null)
    {
        opts ??= new PasswordChangeOptions
        {
            AllowedUsernameAttributes = new[] { "samaccountname", "userprincipalname", "mail" },
            BaseDn = "DC=corp,DC=example,DC=com",
            ServiceAccountDn = "CN=svc,DC=corp,DC=example,DC=com",
            ServiceAccountPassword = "svcpw",
            LdapHostnames = new[] { "dc01.corp.example.com" },
            LdapPort = 636,
        };
        var fake = new FakeLdapSession();
        var sut = new LdapPasswordChangeProvider(
            Options.Create(opts),
            NullLogger<LdapPasswordChangeProvider>.Instance,
            () => fake);
        return (sut, fake);
    }

    private static SearchResponse MakeResponse(params SearchResultEntry[] entries)
    {
        // SearchResponse has no parameterless ctor on .NET 10; use the internal
        // (string dn, DirectoryControl[] controls, ResultCode result, string message, Uri[] referral) overload.
        var response = (SearchResponse)Activator.CreateInstance(
            typeof(SearchResponse),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[] { string.Empty, Array.Empty<DirectoryControl>(), ResultCode.Success, string.Empty, Array.Empty<Uri>() },
            null)!;
        var entriesProp = typeof(SearchResponse).GetProperty("Entries")!;
        var collection = (SearchResultEntryCollection)entriesProp.GetValue(response)!;
        var addMethod = typeof(SearchResultEntryCollection).GetMethod(
            "Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(SearchResultEntry) }, null)!;
        foreach (var e in entries) addMethod.Invoke(collection, new object?[] { e });
        return response;
    }

    private static SearchResultEntry MakeEntry(string dn, params (string Name, string Value)[] attrs)
    {
        // SearchResultEntry has a public (string dn) ctor on .NET 10.
        var entry = (SearchResultEntry)Activator.CreateInstance(
            typeof(SearchResultEntry),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object[] { dn },
            null)!;

        if (attrs is { Length: > 0 })
        {
            var attrCollection = entry.Attributes;
            var addMethod = typeof(SearchResultAttributeCollection).GetMethod(
                "Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(DirectoryAttribute) }, null)!;
            foreach (var group in attrs.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var directoryAttr = new DirectoryAttribute { Name = group.Key };
                foreach (var (_, value) in group) directoryAttr.Add(value);
                addMethod.Invoke(attrCollection, new object?[] { group.Key, directoryAttr });
            }
        }

        return entry;
    }
}
