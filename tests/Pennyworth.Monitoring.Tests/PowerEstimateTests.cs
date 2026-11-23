namespace Pennyworth.Monitoring.Tests;

public class PowerEstimateTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-6700T CPU @ 2.80GHz", 35)]
    [InlineData("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", 91)]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900K", 125)]
    [InlineData("Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", 65)]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", 15)]
    [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz", 15)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-11800H @ 2.30GHz", 45)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700HX", 55)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", 28)]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", 105)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 120)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", 65)]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", 15)]
    [InlineData("AMD Ryzen 9 6900HS with Radeon Graphics", 35)]
    [InlineData("Intel(R) Celeron(R) N5105 @ 2.00GHz", 10)]
    public void Guesses_the_rated_power_from_the_model(string name, double watts)
    {
        Assert.Equal(watts, PowerEstimate.TdpWatts(name));
    }

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz")]
    [InlineData("Apple M2")]
    [InlineData("")]
    public void Does_not_guess_for_unknown_processors(string name)
    {
        Assert.Null(PowerEstimate.TdpWatts(name));
    }

    [Fact]
    public void Scales_from_idle_to_the_rated_power_with_the_load()
    {
        Assert.Equal(3.5, PowerEstimate.Watts(35, 0), 3);
        Assert.Equal(19.25, PowerEstimate.Watts(35, 50), 3);
        Assert.Equal(35, PowerEstimate.Watts(35, 100), 3);
        Assert.Equal(35, PowerEstimate.Watts(35, 140), 3);
    }
}
