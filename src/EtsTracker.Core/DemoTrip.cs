namespace EtsTracker;

/// <summary>
/// Имитация рейса без игры: удобно проверять подсчёт очков и настройки.
/// Запуск: EtsTracker --demo
/// </summary>
public static class DemoTrip
{
    /// <param name="duration">Растянуть поездку на это реальное время (чтобы в окне было видно ход рейса); null — мгновенно.</param>
    public static void Run(TripTracker tracker, TimeSpan? duration = null)
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
            SourceCityId = "helsinki",
            SourceCompany = "Lisette Logistics",
            DestinationCity = "Турку",
            DestinationCityId = "turku",
            DestinationCompany = "Tradeaux",
            PlannedIncome = 7_400,
            PlannedDistanceKm = 420,
            TruckName = "Scania S",
            FuelLiters = 600,
            SpeedLimitKph = 80,
            TruckWear = 0.02f,
            TrailerCount = 1,
            TrailerWear = 0.01f,
            CargoLoaded = true,
            HasDeadline = true,
        };

        // Без заказа → заказ принят
        ulong ts = 1_000_000;
        tracker.Process(baseFrame with { TimestampUs = ts });
        var f = baseFrame with { OnJob = true, TimestampUs = ts += 1_000_000 };
        tracker.Process(f);

        // Едем: один кадр = одна игровая секунда
        double km = 0, fuel = 600, odo = 125_000;
        int second = 0;
        const int deadlineMinutes = 9 * 60;
        // Примерное число кадров, чтобы равномерно растянуть демо на duration
        int totalFrames = (int)(distanceKm / cruiseKph * 3600);
        int framesPerPause = Math.Max(1, totalFrames / 200);
        var pause = duration is { } d ? d / 200 : TimeSpan.Zero;
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
                TruckWear = (float)(0.02 + 0.03 * km / distanceKm),
                TrailerWear = (float)(0.01 + 0.015 * km / distanceKm),
                // Штраф за проезд на красный где-то посередине
                FinedFlag = second is 9000 or 9001,
                FineAmount = 250,
                FineOffence = "Red_signal",
                NavDistanceKm = (float)Math.Max(0, distanceKm - km),
                NavTimeSec = (float)(Math.Max(0, distanceKm - km) / cruiseKph * 3600),
                // Срок в игровых минутах: из 9 часов к финишу остаётся около двух
                DeadlineMinutesLeft = deadlineMinutes - (int)(km / distanceKm * 7 * 60),
            };
            tracker.Process(f);
            if (pause > TimeSpan.Zero && second % framesPerPause == 0) Thread.Sleep(pause);
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
