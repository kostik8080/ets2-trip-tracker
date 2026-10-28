using SCSSdkClient.Object;

namespace EtsTracker;

/// <summary>Переводит объект SDK в наш TelemetryFrame.</summary>
public static class TelemetryMapper
{
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
            HasDeadline = job.DeliveryTime.Value > 0,
            DeadlineMinutesLeft = job.RemainingDeliveryTime.Value,
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
