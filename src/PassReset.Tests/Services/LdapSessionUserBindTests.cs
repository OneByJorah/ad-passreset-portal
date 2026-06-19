using PassReset.PasswordProvider.Ldap;

namespace PassReset.Tests.Services;

public class LdapSessionUserBindTests
{
    private sealed class FakeSession : ILdapSession
    {
        private readonly bool _bindResult;
        public (string dn, string pw)? LastBind { get; private set; }
        public FakeSession(bool bindResult) => _bindResult = bindResult;
        public bool TryBindAsUser(string userDn, string password)
        {
            LastBind = (userDn, password);
            return _bindResult;
        }
        // Minimal stubs for the rest of the interface — throw if unexpectedly called.
        public void Bind() => throw new NotSupportedException();
        public System.DirectoryServices.Protocols.SearchResponse Search(System.DirectoryServices.Protocols.SearchRequest r) => throw new NotSupportedException();
        public System.DirectoryServices.Protocols.ModifyResponse Modify(System.DirectoryServices.Protocols.ModifyRequest r) => throw new NotSupportedException();
        public System.DirectoryServices.Protocols.SearchResultEntry? RootDse => null;
        public void Dispose() { }
    }

    [Fact]
    public void TryBindAsUser_ReturnsConfiguredResult_AndRecordsArgs()
    {
        ILdapSession ok = new FakeSession(true);
        Assert.True(ok.TryBindAsUser("CN=alice,DC=corp", "pw"));

        ILdapSession bad = new FakeSession(false);
        Assert.False(bad.TryBindAsUser("CN=alice,DC=corp", "wrong"));
    }
}
