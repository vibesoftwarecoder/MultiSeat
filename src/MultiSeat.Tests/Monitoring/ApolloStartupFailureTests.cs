using MultiSeat.Service.Monitoring;
using Xunit;

namespace MultiSeat.Tests.Monitoring;

/// <summary>Extra diagnostics are selected by elapsed time, not by an inferred cause.</summary>
public class ApolloStartupFailureTests
{
    [Fact]
    public void NoInstanceRecord_IsNotAStartupFailure()
    {
        // The one that would misfire: null means "never launched", not "died instantly". Treating
        // it as a failure would print the hint for every seat the manager has no record of.
        Assert.False(SessionHealthCheck.IsEarlyExit(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    public void DyingWithinTheStartupWindow_IsAnEarlyExit(int seconds)
    {
        Assert.True(SessionHealthCheck.IsEarlyExit(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-1)]
    [InlineData(120)]
    [InlineData(3600)]
    public void DyingLater_DoesNotNeedTheEarlyExitHint(int seconds)
    {
        // A seat that streamed for a while and then died is a different problem, and the encoder
        // hint would be misleading there.
        Assert.False(SessionHealthCheck.IsEarlyExit(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TheBoundaryItselfCountsAsAnEarlyExit()
    {
        Assert.True(SessionHealthCheck.IsEarlyExit(SessionHealthCheck.ApolloStartupWindow));
    }
}
