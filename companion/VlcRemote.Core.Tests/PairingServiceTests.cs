using Microsoft.Extensions.Time.Testing;
using VlcRemote.Core.Pairing;
using Xunit;

namespace VlcRemote.Core.Tests;

public class PairingServiceTests
{
    [Fact]
    public void Pair_WithCorrectCode_IssuesTokenAndStoresHash()
    {
        var store = new InMemoryTokenStore();
        var svc = new PairingService(store);
        var code = svc.BeginPairing();
        Assert.Matches("^[0-9]{6}$", code);
        Assert.True(svc.IsPairingActive);

        PairedDevice? paired = null;
        svc.Paired += d => paired = d;
        var result = svc.TryPair(code, "Pixel 8", "dev-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Token);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", result.Token); // 32 bytes of base64url without '='
        Assert.True(svc.ValidateToken(result.Token));
        Assert.False(svc.ValidateToken(result.Token + "x"));
        Assert.False(svc.ValidateToken(null));
        Assert.False(svc.IsPairingActive); // the code is single-use
        Assert.Equal(PairingService.Hash(result.Token), Assert.Single(store.Load()).TokenHash);
        Assert.Equal("Pixel 8", paired!.DeviceName);
        Assert.DoesNotContain(result.Token, store.Load().Select(d => d.TokenHash)); // only the hash is stored
    }

    [Fact]
    public void Pair_WrongCode_FailsAndLimitsAttempts()
    {
        var svc = new PairingService(new InMemoryTokenStore()) { MaxAttempts = 3 };
        var code = svc.BeginPairing();
        var wrong = code == "000000" ? "000001" : "000000";
        Assert.False(svc.TryPair(wrong, "x", "y").Success);
        Assert.False(svc.TryPair(wrong, "x", "y").Success);
        Assert.True(svc.IsPairingActive);
        Assert.False(svc.TryPair(wrong, "x", "y").Success);
        Assert.False(svc.IsPairingActive); // attempts used up
        Assert.False(svc.TryPair(code, "x", "y").Success);
    }

    [Fact]
    public void Pair_ExpiresAfterFiveMinutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T20:00:00Z"));
        var svc = new PairingService(new InMemoryTokenStore(), time);
        var code = svc.BeginPairing();
        time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        Assert.True(svc.IsPairingActive);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(svc.IsPairingActive);
        Assert.False(svc.TryPair(code, "x", "y").Success);
    }

    [Fact]
    public void NoPairingActive_Fails()
    {
        var svc = new PairingService(new InMemoryTokenStore());
        var r = svc.TryPair("123456", "x", "y");
        Assert.False(r.Success);
        Assert.Equal("pairing not active", r.Reason);
    }

    [Fact]
    public void RePairingSameDevice_ReplacesToken()
    {
        var store = new InMemoryTokenStore();
        var svc = new PairingService(store);
        var t1 = svc.TryPair(svc.BeginPairing(), "Phone", "dev").Token!;
        var t2 = svc.TryPair(svc.BeginPairing(), "Phone", "dev").Token!;
        Assert.Single(store.Load());
        Assert.False(svc.ValidateToken(t1));
        Assert.True(svc.ValidateToken(t2));
    }

    [Fact]
    public void JsonFileStore_PersistsAcrossInstances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vlcremote-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "paired.json");
        try
        {
            var svc = new PairingService(new JsonFileTokenStore(path));
            var token = svc.TryPair(svc.BeginPairing(), "Phone", "dev").Token!;
            var svc2 = new PairingService(new JsonFileTokenStore(path));
            Assert.True(svc2.ValidateToken(token));
            Assert.True(svc2.HasPairedDevices);
            svc2.ForgetAll();
            Assert.False(new PairingService(new JsonFileTokenStore(path)).ValidateToken(token));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
