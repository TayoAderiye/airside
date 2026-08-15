using Airside.Core.Common;

namespace Airside.Tests.Common;

/// <summary>
/// Slug is the boundary that makes every derived Docker name safe, so it is
/// tested against the things that would make a name dangerous rather than only
/// against the happy path.
/// </summary>
public class SlugTests
{
    [Theory]
    [InlineData("orders")]
    [InlineData("orders-db")]
    [InlineData("a1b")]
    [InlineData("my-app-2")]
    public void TryCreate_ValidCandidate_Succeeds(string candidate)
    {
        Assert.True(Slug.TryCreate(candidate, out var slug));
        Assert.Equal(candidate, slug.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]                       // shorter than the minimum
    [InlineData("1orders")]                  // must start with a letter
    [InlineData("orders-")]                  // must end alphanumeric
    [InlineData("-orders")]
    [InlineData("orders--db")]               // consecutive hyphens
    [InlineData("Orders")]                   // rejected, not lowercased
    [InlineData("orders db")]                // whitespace
    [InlineData("orders/db")]                // path separator
    [InlineData("orders;rm -rf /")]          // shell metacharacters
    [InlineData("orders$(whoami)")]
    [InlineData("../../etc/passwd")]
    [InlineData("orders\nnewline")]
    public void TryCreate_InvalidCandidate_Fails(string? candidate)
    {
        Assert.False(Slug.TryCreate(candidate, out _));
    }

    [Fact]
    public void TryCreate_LongerThanMaximum_Fails()
    {
        var tooLong = "a" + new string('b', Slug.MaxLength);
        Assert.False(Slug.TryCreate(tooLong, out _));
    }

    [Fact]
    public void Create_UppercaseCandidate_RejectsRatherThanNormalising()
    {
        // Reject, do not sanitise. A caller that meant "Orders" should be told,
        // not silently handed "orders" — which may already belong to something else.
        var result = Slug.Create("Orders");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationInvalidSlug, result.Failure!.Code);
    }

    [Fact]
    public void Create_InvalidCandidate_ReportsPatternInMetadata()
    {
        var result = Slug.Create("no");

        Assert.True(result.IsFailure);
        Assert.NotNull(result.Failure!.Metadata);
        Assert.True(result.Failure.Metadata!.ContainsKey("pattern"));
    }
}
