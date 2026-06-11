using PcEnergyMeter.Core;

namespace PcEnergyMeter.Tests;

public sealed class CostCalculatorTests
{
    [Fact]
    public void WattsToKwh_ConvertsOneKilowattHour()
    {
        var kwh = CostCalculator.WattsToKwh(500, TimeSpan.FromHours(2));

        Assert.Equal(1, kwh, precision: 6);
    }

    [Fact]
    public void ProjectCost_UsesConfiguredTariff()
    {
        var cost = CostCalculator.ProjectCost(1000, TimeSpan.FromHours(10), 0.127758m);

        Assert.Equal(1.27758m, cost);
    }
}
