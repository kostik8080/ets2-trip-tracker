using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EtsTracker;

/// <summary>Хранит рейсы в SQLite (data/trips.db).</summary>
public sealed class TripRepository
{
    private readonly string _connectionString;

    public TripRepository(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS trips (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                status           TEXT    NOT NULL,
                started_at       TEXT    NOT NULL,
                finished_at      TEXT    NOT NULL,
                cargo_id         TEXT,
                cargo_name       TEXT,
                cargo_mass_t     REAL,
                cargo_traits     TEXT,
                special_job      INTEGER,
                from_place       TEXT,
                to_place         TEXT,
                truck            TEXT,
                job_distance_km  REAL,
                driven_km        REAL,
                fuel_used_l      REAL,
                revenue          INTEGER,
                earned_xp        INTEGER,
                cargo_damage     REAL,
                auto_parked      INTEGER,
                fines_count      INTEGER,
                fines_total      INTEGER,
                tolls_total      INTEGER,
                speeding_seconds REAL,
                max_speed_kph    REAL,
                started_mid_job  INTEGER,
                score            INTEGER,
                grade            TEXT,
                breakdown_json   TEXT
            );
            """);
    }

    public void Save(TripResult t)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trips (status, started_at, finished_at, cargo_id, cargo_name, cargo_mass_t,
                cargo_traits, special_job, from_place, to_place, truck, job_distance_km, driven_km,
                fuel_used_l, revenue, earned_xp, cargo_damage, auto_parked, fines_count, fines_total,
                tolls_total, speeding_seconds, max_speed_kph, started_mid_job, score, grade, breakdown_json)
            VALUES ($status, $started, $finished, $cargoId, $cargoName, $mass,
                $traits, $special, $from, $to, $truck, $jobKm, $drivenKm,
                $fuel, $revenue, $xp, $damage, $autoParked, $finesCount, $finesTotal,
                $tolls, $speeding, $maxSpeed, $midJob, $score, $grade, $breakdown);
            SELECT last_insert_rowid();
            """;
        var p = cmd.Parameters;
        p.AddWithValue("$status", t.Status.ToString());
        p.AddWithValue("$started", t.StartedAt.ToString("O"));
        p.AddWithValue("$finished", t.FinishedAt.ToString("O"));
        p.AddWithValue("$cargoId", t.CargoId);
        p.AddWithValue("$cargoName", t.CargoName);
        p.AddWithValue("$mass", t.CargoMassT);
        p.AddWithValue("$traits", t.Traits.ToString());
        p.AddWithValue("$special", t.SpecialJob);
        p.AddWithValue("$from", t.From);
        p.AddWithValue("$to", t.To);
        p.AddWithValue("$truck", t.TruckName);
        p.AddWithValue("$jobKm", t.JobDistanceKm);
        p.AddWithValue("$drivenKm", t.DrivenKm);
        p.AddWithValue("$fuel", t.FuelUsedL);
        p.AddWithValue("$revenue", t.Revenue);
        p.AddWithValue("$xp", t.EarnedXp);
        p.AddWithValue("$damage", t.CargoDamage);
        p.AddWithValue("$autoParked", t.AutoParked);
        p.AddWithValue("$finesCount", t.FinesCount);
        p.AddWithValue("$finesTotal", t.FinesTotal);
        p.AddWithValue("$tolls", t.TollsTotal);
        p.AddWithValue("$speeding", t.SpeedingSeconds);
        p.AddWithValue("$maxSpeed", t.MaxSpeedKph);
        p.AddWithValue("$midJob", t.StartedMidJob);
        p.AddWithValue("$score", t.Score);
        p.AddWithValue("$grade", t.Grade);
        p.AddWithValue("$breakdown", JsonSerializer.Serialize(t.Breakdown, ConfigStore.Json));
        t.Id = (long)cmd.ExecuteScalar()!;
    }

    public CareerStats GetStats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN status = 'Delivered' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(score), 0),
                   COALESCE(SUM(driven_km), 0),
                   COALESCE(SUM(fuel_used_l), 0),
                   COALESCE(SUM(revenue), 0),
                   COALESCE(SUM(cargo_mass_t * (status = 'Delivered')), 0),
                   COALESCE(SUM(CASE WHEN status = 'Delivered' AND cargo_damage < 0.005 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(fines_count), 0)
            FROM trips;
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new CareerStats(
            r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetDouble(3), r.GetDouble(4),
            r.GetInt64(5), r.GetDouble(6), r.GetInt32(7), r.GetInt32(8));
    }

    public List<(DateTime When, string Cargo, string Route, int Score, string Grade)> GetRecent(int count)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT finished_at, cargo_name, from_place, to_place, score, grade
            FROM trips ORDER BY id DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", count);
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, string, string, int, string)>();
        while (r.Read())
        {
            list.Add((DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetString(1), $"{r.GetString(2)} → {r.GetString(3)}", r.GetInt32(4), r.GetString(5)));
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public sealed record CareerStats(
    int Trips, int Delivered, long TotalScore, double TotalKm, double TotalFuelL,
    long TotalRevenue, double TotalTonnes, int PerfectDeliveries, int TotalFines);
