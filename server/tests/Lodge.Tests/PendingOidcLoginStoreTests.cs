using Lodge.Infrastructure.Auth;
using Xunit;

namespace Lodge.Tests;

public class PendingOidcLoginStoreTests
{
    [Fact]
    public void TryTake_returns_the_added_login_exactly_once()
    {
        var store = new PendingOidcLoginStore();
        var login = new PendingOidcLogin("verifier", "https://ui/callback", null, DateTimeOffset.UtcNow);

        store.Add("state-1", login);

        Assert.True(store.TryTake("state-1", out var taken));
        Assert.Equal(login, taken);
        Assert.False(store.TryTake("state-1", out _), "a state must only be redeemable once");
    }

    [Fact]
    public void TryTake_fails_for_an_unknown_state()
    {
        var store = new PendingOidcLoginStore();
        Assert.False(store.TryTake("never-added", out _));
    }

    [Fact]
    public void TryTake_fails_once_the_entry_has_expired()
    {
        var store = new PendingOidcLoginStore(ttl: TimeSpan.FromMilliseconds(1));
        var login = new PendingOidcLogin("verifier", null, "http://127.0.0.1:1/callback", DateTimeOffset.UtcNow);
        store.Add("state-1", login);

        Thread.Sleep(50);

        Assert.False(store.TryTake("state-1", out _));
    }
}
