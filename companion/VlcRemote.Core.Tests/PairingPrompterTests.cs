using Microsoft.Extensions.Time.Testing;
using VlcRemote.Core.Pairing;
using Xunit;

namespace VlcRemote.Core.Tests;

public class PairingPrompterTests
{
    private static (PairingService pairing, PairingPrompter prompter, FakeTimeProvider time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-13T12:00:00Z"));
        var pairing = new PairingService(new InMemoryTokenStore(), time);
        return (pairing, new PairingPrompter(pairing, time), time);
    }

    [Fact]
    public void FirstRequest_CreatesCode()
    {
        var (pairing, prompter, _) = Create();
        var p = prompter.Request("Pixel 8", "dev-1");
        Assert.Equal(PairingPromptAction.ShowNew, p.Action);
        Assert.Matches("^[0-9]{6}$", p.Code);
        Assert.Equal("Pixel 8", p.DeviceName);
        Assert.Equal(p.Code, pairing.ActiveCode);
    }

    [Fact]
    public void ActiveCode_IsNotReplaced_OtherDeviceBringsWindowToFront()
    {
        var (pairing, prompter, time) = Create();
        var first = prompter.Request("Pixel 8", "dev-1");
        var second = prompter.Request("Galaxy", "dev-2");
        Assert.Equal(PairingPromptAction.BringToFront, second.Action);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Code, pairing.ActiveCode);

        // The same phone after a pause: the code is still valid — the window is only raised.
        time.Advance(TimeSpan.FromSeconds(11));
        var again = prompter.Request("Pixel 8", "dev-1");
        Assert.Equal(PairingPromptAction.BringToFront, again.Action);
        Assert.Equal(first.Code, pairing.ActiveCode);
    }

    [Fact]
    public void SameDevice_IsThrottledFor10Seconds()
    {
        var (pairing, prompter, time) = Create();
        prompter.Request("Pixel 8", "dev-1");
        pairing.CancelPairing(); // the user closed the window, the phone keeps knocking

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(PairingPromptAction.Throttled, prompter.Request("Pixel 8", "dev-1").Action);
        Assert.Null(pairing.ActiveCode);

        time.Advance(TimeSpan.FromSeconds(8));
        var p = prompter.Request("Pixel 8", "dev-1");
        Assert.Equal(PairingPromptAction.ShowNew, p.Action);
        Assert.Equal(p.Code, pairing.ActiveCode);
    }

    [Fact]
    public void NewCodes_AreLimitedGlobally()
    {
        var (pairing, prompter, time) = Create();
        prompter.Request("A", "a");
        pairing.CancelPairing();
        time.Advance(TimeSpan.FromSeconds(1));
        // Another "phone" right after — no new code is created (brute-force protection).
        Assert.Equal(PairingPromptAction.Throttled, prompter.Request("B", "b").Action);
        Assert.Null(pairing.ActiveCode);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PairingPromptAction.ShowNew, prompter.Request("B", "b").Action);
    }

    [Fact]
    public void ExpiredCode_GetsReplaced()
    {
        var (pairing, prompter, time) = Create();
        var first = prompter.Request("Pixel 8", "dev-1");
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(pairing.ActiveCode);
        var p = prompter.Request("Pixel 8", "dev-1");
        Assert.Equal(PairingPromptAction.ShowNew, p.Action);
        Assert.Equal(p.Code, pairing.ActiveCode);
        Assert.NotNull(first.Code);
    }
}
