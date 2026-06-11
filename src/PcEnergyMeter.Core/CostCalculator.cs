namespace PcEnergyMeter.Core;

public static class CostCalculator
{
    public static double WattsToKwh(double watts, TimeSpan duration)
    {
        if (watts <= 0 || duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return watts * duration.TotalHours / 1000d;
    }

    public static decimal KwhToEur(double kwh, decimal eurPerKwh)
    {
        if (kwh <= 0 || eurPerKwh <= 0)
        {
            return 0m;
        }

        return (decimal)kwh * eurPerKwh;
    }

    public static decimal ProjectCost(double watts, TimeSpan duration, decimal eurPerKwh)
    {
        return KwhToEur(WattsToKwh(watts, duration), eurPerKwh);
    }
}
