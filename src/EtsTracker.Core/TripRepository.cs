using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EtsTracker;

/// <summary>Хранит водителей и рейсы в SQLite (data/trips.db).</summary>
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
            CREATE TABLE IF NOT EXISTS drivers (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                nickname           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                reg_number         TEXT,
                truck              TEXT,
                rank               TEXT    NOT NULL,
                permit_adr         INTEGER NOT NULL DEFAULT 0,
                permit_heavy       INTEGER NOT NULL DEFAULT 0,
                permit_articulated INTEGER NOT NULL DEFAULT 0,
                created_at         TEXT    NOT NULL
            );
            """);

        // Колонки, добавленные в версии 0.2 (базы из 0.1 дополняются без потери рейсов)
        AddColumnIfMissing(c, "trips", "driver_id", "INTEGER REFERENCES drivers(id)");
        AddColumnIfMissing(c, "trips", "trip_kind", "TEXT NOT NULL DEFAULT 'Solo'");
        AddColumnIfMissing(c, "trips", "cargo_class", "TEXT NOT NULL DEFAULT 'Normal'");
        AddColumnIfMissing(c, "trips", "from_city_id", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(c, "trips", "to_city_id", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(c, "trips", "trailer_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "trips", "truck_damage", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "trips", "trailer_damage", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "trips", "salary", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "trips", "salary_json", "TEXT NOT NULL DEFAULT '[]'");
        AddColumnIfMissing(c, "drivers", "is_admin", "INTEGER NOT NULL DEFAULT 0");
        EnsureAdmin(c);
    }

    /// <summary>Если администратора нет, им становится первый зарегистрированный водитель.</summary>
    private static void EnsureAdmin(SqliteConnection c)
        => Exec(c, """
            UPDATE drivers SET is_admin = 1
            WHERE id = (SELECT MIN(id) FROM drivers)
              AND NOT EXISTS (SELECT 1 FROM drivers WHERE is_admin = 1);
            """);

    // ───────────────────────────── Водители ─────────────────────────────

    public List<Driver> GetDrivers()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, nickname, reg_number, truck, rank, permit_adr, permit_heavy, permit_articulated, created_at, is_admin
            FROM drivers ORDER BY reg_number, nickname;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<Driver>();
        while (r.Read())
        {
            list.Add(new Driver
            {
                Id = r.GetInt64(0),
                Nickname = r.GetString(1),
                RegNumber = r.IsDBNull(2) ? "" : r.GetString(2),
                Truck = r.IsDBNull(3) ? "" : r.GetString(3),
                Rank = Enum.TryParse<DriverRank>(r.GetString(4), out var rank) ? rank : DriverRank.Trainee,
                PermitAdr = r.GetBoolean(5),
                PermitHeavy = r.GetBoolean(6),
                PermitArticulated = r.GetBoolean(7),
                CreatedAt = ParseDate(r.GetString(8)),
                IsAdmin = r.GetBoolean(9),
            });
        }
        return list;
    }

    public bool NicknameTaken(string nickname, long exceptId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM drivers WHERE nickname = $n COLLATE NOCASE AND id <> $id;";
        cmd.Parameters.AddWithValue("$n", nickname.Trim());
        cmd.Parameters.AddWithValue("$id", exceptId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>Сохраняет нового водителя. Первому водителю достаются рейсы, записанные до регистрации.</summary>
    public void CreateDriver(Driver d)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO drivers (nickname, reg_number, truck, rank, permit_adr, permit_heavy, permit_articulated, is_admin, created_at)
            VALUES ($nick, $reg, $truck, $rank, $adr, $heavy, $art, $admin, $created);
            SELECT last_insert_rowid();
            """;
        d.CreatedAt = DateTime.Now;
        AddDriverParams(cmd, d);
        cmd.Parameters.AddWithValue("$created", d.CreatedAt.ToString("O"));
        d.Id = (long)cmd.ExecuteScalar()!;

        using var adopt = c.CreateCommand();
        adopt.Transaction = tx;
        adopt.CommandText = """
            UPDATE trips SET driver_id = $id
            WHERE driver_id IS NULL AND (SELECT COUNT(*) FROM drivers) = 1;
            """;
        adopt.Parameters.AddWithValue("$id", d.Id);
        adopt.ExecuteNonQuery();
        tx.Commit();

        EnsureAdmin(c);
        using var check = c.CreateCommand();
        check.CommandText = "SELECT is_admin FROM drivers WHERE id = $id;";
        check.Parameters.AddWithValue("$id", d.Id);
        d.IsAdmin = (long)check.ExecuteScalar()! == 1;
    }

    /// <summary>Сколько администраторов, не считая указанного водителя.</summary>
    public int OtherAdminsCount(long exceptId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM drivers WHERE is_admin = 1 AND id <> $id;";
        cmd.Parameters.AddWithValue("$id", exceptId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public void UpdateDriver(Driver d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE drivers SET nickname = $nick, reg_number = $reg, truck = $truck, rank = $rank,
                permit_adr = $adr, permit_heavy = $heavy, permit_articulated = $art, is_admin = $admin
            WHERE id = $id;
            """;
        AddDriverParams(cmd, d);
        cmd.Parameters.AddWithValue("$id", d.Id);
        cmd.ExecuteNonQuery();
    }

    private static void AddDriverParams(SqliteCommand cmd, Driver d)
    {
        var p = cmd.Parameters;
        p.AddWithValue("$nick", d.Nickname.Trim());
        p.AddWithValue("$reg", d.RegNumber.Trim());
        p.AddWithValue("$truck", d.Truck.Trim());
        p.AddWithValue("$rank", d.Rank.ToString());
        p.AddWithValue("$adr", d.PermitAdr);
        p.AddWithValue("$heavy", d.PermitHeavy);
        p.AddWithValue("$art", d.PermitArticulated);
        p.AddWithValue("$admin", d.IsAdmin);
    }

    // ────────────────────────────── Рейсы ──────────────────────────────

    public void Save(TripResult t)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trips (status, started_at, finished_at, cargo_id, cargo_name, cargo_mass_t,
                cargo_traits, special_job, from_place, to_place, truck, job_distance_km, driven_km,
                fuel_used_l, revenue, earned_xp, cargo_damage, auto_parked, fines_count, fines_total,
                tolls_total, speeding_seconds, max_speed_kph, started_mid_job, score, grade, breakdown_json,
                driver_id, trip_kind, cargo_class, from_city_id, to_city_id, trailer_count,
                truck_damage, trailer_damage, salary, salary_json)
            VALUES ($status, $started, $finished, $cargoId, $cargoName, $mass,
                $traits, $special, $from, $to, $truck, $jobKm, $drivenKm,
                $fuel, $revenue, $xp, $damage, $autoParked, $finesCount, $finesTotal,
                $tolls, $speeding, $maxSpeed, $midJob, $score, $grade, $breakdown,
                $driver, $kind, $class, $fromId, $toId, $trailers,
                $truckDamage, $trailerDamage, $salary, $salaryJson);
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
        p.AddWithValue("$driver", (object?)t.DriverId ?? DBNull.Value);
        p.AddWithValue("$kind", t.Kind.ToString());
        p.AddWithValue("$class", t.CargoClass.ToString());
        p.AddWithValue("$fromId", t.FromCityId);
        p.AddWithValue("$toId", t.ToCityId);
        p.AddWithValue("$trailers", t.TrailerCount);
        p.AddWithValue("$truckDamage", t.TruckDamage);
        p.AddWithValue("$trailerDamage", t.TrailerDamage);
        p.AddWithValue("$salary", t.Salary);
        p.AddWithValue("$salaryJson", JsonSerializer.Serialize(t.SalaryBreakdown, ConfigStore.Json));
        t.Id = (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Сохраняет поправки, сделанные вручную: тип поездки, класс груза и пересчитанную зарплату.</summary>
    public void UpdateClassification(TripResult t)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE trips SET trip_kind = $kind, cargo_class = $class, salary = $salary, salary_json = $salaryJson
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$kind", t.Kind.ToString());
        cmd.Parameters.AddWithValue("$class", t.CargoClass.ToString());
        cmd.Parameters.AddWithValue("$salary", t.Salary);
        cmd.Parameters.AddWithValue("$salaryJson", JsonSerializer.Serialize(t.SalaryBreakdown, ConfigStore.Json));
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTrip(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM trips WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сколько доставленных рейсов у водителя было в тот же день раньше указанного момента.</summary>
    public int CountDeliveredSameDayBefore(long? driverId, DateTime finishedAt, long exceptTripId = 0)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM trips
            WHERE status = 'Delivered' AND driver_id IS $driver AND id <> $except
              AND finished_at >= $dayStart AND finished_at < $at;
            """;
        cmd.Parameters.AddWithValue("$driver", (object?)driverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$except", exceptTripId);
        cmd.Parameters.AddWithValue("$dayStart", finishedAt.Date.ToString("O"));
        cmd.Parameters.AddWithValue("$at", finishedAt.ToString("O"));
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Все рейсы водителя (null — рейсы без водителя), новые первыми.</summary>
    public List<TripResult> GetTrips(long? driverId, int limit = int.MaxValue)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, started_at, finished_at, cargo_id, cargo_name, cargo_mass_t, cargo_traits,
                special_job, from_place, to_place, truck, job_distance_km, driven_km, fuel_used_l, revenue,
                earned_xp, cargo_damage, auto_parked, fines_count, fines_total, tolls_total, speeding_seconds,
                max_speed_kph, started_mid_job, score, grade, breakdown_json,
                driver_id, trip_kind, cargo_class, from_city_id, to_city_id, trailer_count,
                truck_damage, trailer_damage, salary, salary_json
            FROM trips WHERE driver_id IS $driver ORDER BY id DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$driver", (object?)driverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TripResult>();
        while (r.Read())
        {
            list.Add(new TripResult
            {
                Id = r.GetInt64(0),
                Status = Enum.TryParse<TripStatus>(r.GetString(1), out var st) ? st : TripStatus.Delivered,
                StartedAt = ParseDate(r.GetString(2)),
                FinishedAt = ParseDate(r.GetString(3)),
                CargoId = Str(r, 4),
                CargoName = Str(r, 5),
                CargoMassT = Dbl(r, 6),
                Traits = Enum.TryParse<CargoTraits>(Str(r, 7), out var tr) ? tr : CargoTraits.None,
                SpecialJob = !r.IsDBNull(8) && r.GetBoolean(8),
                From = Str(r, 9),
                To = Str(r, 10),
                TruckName = Str(r, 11),
                JobDistanceKm = Dbl(r, 12),
                DrivenKm = Dbl(r, 13),
                FuelUsedL = Dbl(r, 14),
                Revenue = r.IsDBNull(15) ? 0 : r.GetInt64(15),
                EarnedXp = r.IsDBNull(16) ? 0 : r.GetInt32(16),
                CargoDamage = Dbl(r, 17),
                AutoParked = !r.IsDBNull(18) && r.GetBoolean(18),
                FinesCount = r.IsDBNull(19) ? 0 : r.GetInt32(19),
                FinesTotal = r.IsDBNull(20) ? 0 : r.GetInt64(20),
                TollsTotal = r.IsDBNull(21) ? 0 : r.GetInt64(21),
                SpeedingSeconds = Dbl(r, 22),
                MaxSpeedKph = Dbl(r, 23),
                StartedMidJob = !r.IsDBNull(24) && r.GetBoolean(24),
                Score = r.IsDBNull(25) ? 0 : r.GetInt32(25),
                Grade = Str(r, 26),
                Breakdown = Lines(Str(r, 27)),
                DriverId = r.IsDBNull(28) ? null : r.GetInt64(28),
                Kind = Enum.TryParse<TripKind>(r.GetString(29), out var k) ? k : TripKind.Solo,
                CargoClass = Enum.TryParse<CargoClass>(r.GetString(30), out var cc) ? cc : CargoClass.Normal,
                FromCityId = r.GetString(31),
                ToCityId = r.GetString(32),
                TrailerCount = r.GetInt32(33),
                TruckDamage = r.GetDouble(34),
                TrailerDamage = r.GetDouble(35),
                Salary = r.GetDouble(36),
                SalaryBreakdown = Lines(r.GetString(37)),
            });
        }
        return list;
    }

    public CareerStats GetStats(long? driverId)
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
            FROM trips WHERE driver_id IS $driver;
            """;
        cmd.Parameters.AddWithValue("$driver", (object?)driverId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new CareerStats(
            r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetDouble(3), r.GetDouble(4),
            r.GetInt64(5), r.GetDouble(6), r.GetInt32(7), r.GetInt32(8));
    }

    // ───────────────────────────── Служебное ─────────────────────────────

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    private static DateTime ParseDate(string s)
        => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static List<ScoreLine> Lines(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ScoreLine>>(json, ConfigStore.Json) ?? new(); }
        catch (JsonException) { return new(); }
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

    private static void AddColumnIfMissing(SqliteConnection c, string table, string column, string definition)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if ((long)cmd.ExecuteScalar()! == 0)
            Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }
}

public sealed record CareerStats(
    int Trips, int Delivered, long TotalScore, double TotalKm, double TotalFuelL,
    long TotalRevenue, double TotalTonnes, int PerfectDeliveries, int TotalFines);
