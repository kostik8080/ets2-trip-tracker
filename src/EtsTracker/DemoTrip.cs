namespace EtsTracker;

/// <summary>
/// Имитация рейса без игры: удобно проверять подсчёт очков и настройки.
/// Запуск: EtsTracker --demo
/// </summary>
public static class DemoTrip
{
    public static void Run(TripTracker tracker)
    {
        const double distanceKm = 420;
        const double cruiseKph = 85;
        const double fuelPer100 = 33;

        var baseFrame = new TelemetryFrame
        {
            SdkActive = true,
            CargoId = "glass_pck",
            CargoName = "Стекло",
            CargoMassKg = 18_200,
            SourceCity = "Хельсинки",
            SourceCompany = "Lisette Logistics",
            DestinationCity = "Турку",
            DestinationCompany = "Tradeaux",
            PlannedIncome = 7_400,
            PlannedDistanceKm = 420,
            TruckName = "Scania S",
            FuelLiters = 600,
            SpeedLimitKph = 80,
        };

        // Без заказа → заказ принят
        ulong ts = 1_000_000;
        tracker.Process(baseFrame with { TimestampUs = ts });
        var f = baseFrame with { OnJob = true, TimestampUs = ts += 1_000_000 };
        tracker.Process(f);

        // Едем: один кадр = одна игровая секунда
        double km = 0, fuel = 600, odo = 125_000;
        int second = 0;
        while (km < distanceKm)
        {
            second++;
            // Минута превышения в середине пути
            double speed = second is > 6000 and < 6060 ? 97 : cruiseKph;
            double dKm = speed / 3600;
            km += dKm; odo += dKm; fuel -= dKm * fuelPer100 / 100;

            f = f with
            {
                TimestampUs = ts += 1_000_000,
                OdometerKm = (float)odo,
                FuelLiters = (float)fuel,
                SpeedKph = (float)speed,
                CargoDamage = (float)(0.012 * km / distanceKm),
                // Штраф за проезд на красный где-то посередине
                FinedFlag = second is 9000 or 9001,
                FineAmount = 250,
                FineOffence = "Red_signal",
            };
            tracker.Process(f);
        }

        // Доставка
        tracker.Process(f with
        {
            TimestampUs = ts += 1_000_000,
            SpeedKph = 0,
            OnJob = false,
            JobDeliveredFlag = true,
            Delivered = new DeliveredInfo(
                Revenue: 7_400, DistanceKm: (float)distanceKm, CargoDamage: 0.012f,
                EarnedXp: 512, AutoParked: false, AutoLoaded: false),
        });
    }
}
