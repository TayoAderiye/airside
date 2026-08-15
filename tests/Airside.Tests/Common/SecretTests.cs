using Airside.Core.Common;

namespace Airside.Tests.Common;

public class SecretTests
{
    [Fact]
    public void ToString_ReturnsMask()
    {
        var secret = new Secret("hunter2");

        Assert.Equal(Secret.Mask, secret.ToString());
    }

    [Fact]
    public void StringInterpolation_DoesNotLeakValue()
    {
        // The careless thing has to be the safe thing: this is how secrets escape
        // in practice — someone reaching for interpolation while debugging.
        var secret = new Secret("hunter2");

        var message = $"connecting with {secret}";

        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
        Assert.Contains(Secret.Mask, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_ReturnsUnderlyingValue()
    {
        var secret = new Secret("hunter2");

        Assert.Equal("hunter2", secret.Reveal());
    }
}
