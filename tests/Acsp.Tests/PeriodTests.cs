using Acsp.Core;

namespace Acsp.Tests;

public class PeriodTests
{
    [Fact]
    public void Weekly_period_has_10080_minutes() => Assert.Equal(10080, Period.Weekly.N);

    [Theory]
    [InlineData(100, 200, 100)]      // forward within period
    [InlineData(200, 100, 10080 - 100)] // wraps to next period
    [InlineData(0, 0, 0)]
    [InlineData(10079, 0, 1)]
    public void Time_wraps_correctly(int t1, int t2, int expected) =>
        Assert.Equal(expected, Period.Weekly.Time(t1, t2));

    [Theory]
    [InlineData(10080, 0)]
    [InlineData(-1, 10079)]
    [InlineData(20161, 1)]
    [InlineData(5, 5)]
    public void Wrap_normalizes_into_period(int t, int expected) =>
        Assert.Equal(expected, Period.Weekly.Wrap(t));
}
