using SCSSdkClient.Object;

namespace EtsTracker;

/// <summary>Переводит объект SDK в наш TelemetryFrame.</summary>
public static class TelemetryMapper
{
    /// <summary>
    /// Срок доставки известен не всегда: для внешних контрактов (external_contracts) и заказов
    /// без срока игра передаёт «пусто» — 0 или uint.MaxValue. Тогда срок не показываем.
    /// </summary>
    private static bool HasDeadline(uint deliveryTime, uint gameTime)
        => deliveryTime > 0 && deliveryTime != uint.MaxValue && gameTime > 0;

    public static TelemetryFrame Map(SCSTelemetry t)
    {
        var job = t.JobValues;
        var cargo = job.CargoValues;
        var dash = t.TruckValues.CurrentValues.DashboardValues;
        var events = t.SpecialEventsValues;
        var gp = t.GamePlay;
        var truck = t.TruckValues.ConstantsValues;

        var delivered = gp.JobDelivered;
        var wear = t.TruckValues.CurrentValues.DamageValues;
        var trailers = (t.TrailerValues ?? Array.Empty<SCSTelemetry.Trailer>())
            .Where(x => x is { Attached: true })
            .ToList();

        return new TelemetryFrame
        {
            SdkActive = t.SdkActive,
            Paused = t.Paused,
            TimestampUs = t.Timestamp,
            GameTimeMinutes = t.CommonValues.GameTime.Value,

            OnJob = events.OnJob,
            JobDeliveredFlag = events.JobDelivered,
            JobCancelledFlag = events.JobCancelled,
            FinedFlag = events.Fined,
            TollgateFlag = events.Tollgate,

            CargoId = cargo.Id ?? "",
            CargoName = cargo.Name ?? "",
            CargoMassKg = cargo.Mass,
            CargoDamage = cargo.CargoDamage,
            SpecialJob = job.SpecialJob,
            SourceCity = job.CitySource ?? "",
            SourceCityId = job.CitySourceId ?? "",
            SourceCompany = job.CompanySource ?? "",
            DestinationCity = job.CityDestination ?? "",
            DestinationCityId = job.CityDestinationId ?? "",
            DestinationCompany = job.CompanyDestination ?? "",
            PlannedIncome = job.Income,
            PlannedDistanceKm = job.PlannedDistanceKm,
            CargoLoaded = job.CargoLoaded,
            HasDeadline = HasDeadline(job.DeliveryTime.Value, t.CommonValues.GameTime.Value),
            DeadlineMinutesLeft = (int)((long)job.DeliveryTime.Value - t.CommonValues.GameTime.Value),
            NavDistanceKm = t.NavigationValues.NavigationDistance / 1000f,
            NavTimeSec = t.NavigationValues.NavigationTime,

            TruckName = $"{truck.Brand} {truck.Name}".Trim(),
            OdometerKm = dash.Odometer,
            FuelLiters = dash.FuelValue.Amount,
            SpeedKph = Math.Abs(dash.Speed.Kph),
            SpeedLimitKph = t.NavigationValues.SpeedLimit.Kph,
            TruckWear = new[] { wear.Engine, wear.Transmission, wear.Cabin, wear.Chassis, wear.WheelsAvg }.Max(),

            TrailerCount = trailers.Count,
            TrailerWear = trailers.Count == 0 ? 0 : trailers
                .Max(x => Math.Max(x.DamageValues.Body, Math.Max(x.DamageValues.Chassis, x.DamageValues.Wheels))),

            Delivered = new DeliveredInfo(
                delivered.Revenue,
                delivered.DistanceKm,
                delivered.CargoDamage,
                delivered.EarnedXp,
                delivered.AutoParked,
                delivered.AutoLoaded),
            CancelPenalty = gp.JobCancelled.Penalty,
            FineAmount = gp.FinedEvent.Amount,
            FineOffence = gp.FinedEvent.Offence.ToString(),
            TollAmount = gp.TollgateEvent.PayAmount,
        };
    }
}
