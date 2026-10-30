using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace EtsTracker.Server;

/// <summary>Хранит водителей, приглашения и рейсы компании в PostgreSQL.</summary>
public sealed class ServerDb
{
    private readonly NpgsqlDataSource _source;

    public ServerDb(string connectionString) => _source = NpgsqlDataSource.Create(connectionString);

    /// <summary>Создаёт таблицы. База может подниматься дольше сервера, поэтому пробуем несколько раз.</summary>
    public async Task InitAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = _source.CreateCommand("""
                    CREATE TABLE IF NOT EXISTS drivers (
                        id                 BIGSERIAL PRIMARY KEY,
                        nickname           TEXT NOT NULL,
                        password_hash      TEXT NOT NULL,
                        rank               TEXT NOT NULL,
                        permit_adr         BOOLEAN NOT NULL DEFAULT FALSE,
                        permit_heavy       BOOLEAN NOT NULL DEFAULT FALSE,
                        permit_articulated BOOLEAN NOT NULL DEFAULT FALSE,
                        is_admin           BOOLEAN NOT NULL DEFAULT FALSE,
                        created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS drivers_nickname ON drivers (lower(nickname));

                    CREATE TABLE IF NOT EXISTS tokens (
                        token_hash TEXT PRIMARY KEY,
                        driver_id  BIGINT NOT NULL REFERENCES drivers(id) ON DELETE CASCADE,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT now()
                    );

                    CREATE TABLE IF NOT EXISTS invites (
                        code       TEXT PRIMARY KEY,
                        rank       TEXT NOT NULL,
                        is_admin   BOOLEAN NOT NULL DEFAULT FALSE,
                        created_by BIGINT REFERENCES drivers(id) ON DELETE SET NULL,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                        used_by    BIGINT REFERENCES drivers(id) ON DELETE SET NULL,
                        used_at    TIMESTAMPTZ
                    );

                    CREATE TABLE IF NOT EXISTS trips (
                        id               BIGSERIAL PRIMARY KEY,
                        client_id        UUID NOT NULL,
                        driver_id        BIGINT NOT NULL REFERENCES drivers(id) ON DELETE CASCADE,
                        status           TEXT NOT NULL,
                        kind             TEXT NOT NULL,
                        cargo_class      TEXT NOT NULL,
                        started_at       TIMESTAMPTZ NOT NULL,
                        finished_at      TIMESTAMPTZ NOT NULL,
                        received_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                        cargo_id         TEXT NOT NULL,
                        cargo_name       TEXT NOT NULL,
                        cargo_mass_t     DOUBLE PRECISION NOT NULL,
                        cargo_traits     TEXT NOT NULL,
                        special_job      BOOLEAN NOT NULL,
                        from_place       TEXT NOT NULL,
                        to_place         TEXT NOT NULL,
                        from_city_id     TEXT NOT NULL,
                        to_city_id       TEXT NOT NULL,
                        truck            TEXT NOT NULL,
                        trailer_count    INT NOT NULL,
                        job_distance_km  DOUBLE PRECISION NOT NULL,
                        driven_km        DOUBLE PRECISION NOT NULL,
                        fuel_used_l      DOUBLE PRECISION NOT NULL,
                        revenue          BIGINT NOT NULL,
                        earned_xp        INT NOT NULL,
                        cargo_damage     DOUBLE PRECISION NOT NULL,
                        truck_damage     DOUBLE PRECISION NOT NULL,
                        trailer_damage   DOUBLE PRECISION NOT NULL,
                        auto_parked      BOOLEAN NOT NULL,
                        fines_count      INT NOT NULL,
                        fines_total      BIGINT NOT NULL,
                        tolls_total      BIGINT NOT NULL,
                        speeding_seconds DOUBLE PRECISION NOT NULL,
                        max_speed_kph    DOUBLE PRECISION NOT NULL,
                        started_mid_job  BOOLEAN NOT NULL,
                        score            INT NOT NULL,
                        grade            TEXT NOT NULL,
                        breakdown_json   TEXT NOT NULL,
                        salary           DOUBLE PRECISION NOT NULL,
                        salary_json      TEXT NOT NULL,
                        UNIQUE (driver_id, client_id)
                    );
                    CREATE INDEX IF NOT EXISTS trips_finished ON trips (finished_at);

                    CREATE TABLE IF NOT EXISTS bonuses (
                        id         BIGSERIAL PRIMARY KEY,
                        driver_id  BIGINT NOT NULL REFERENCES drivers(id) ON DELETE CASCADE,
                        amount     DOUBLE PRECISION NOT NULL,
                        reason     TEXT NOT NULL,
                        created_by BIGINT REFERENCES drivers(id) ON DELETE SET NULL,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                        -- Отмена не стирает запись: это деньги, история должна остаться
                        deleted_by BIGINT REFERENCES drivers(id) ON DELETE SET NULL,
                        deleted_at TIMESTAMPTZ
                    );
                    CREATE INDEX IF NOT EXISTS bonuses_driver ON bonuses (driver_id);
                    """);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                Console.WriteLine($"База ещё не готова, попытка {attempt}…");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    // ───────────────────────────── Водители ─────────────────────────────

    public async Task<int> CountDriversAsync()
    {
        await using var cmd = _source.CreateCommand("SELECT COUNT(*) FROM drivers;");
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private const string DriverColumns =
        "id, nickname, rank, permit_adr, permit_heavy, permit_articulated, is_admin, created_at";

    private static Driver ReadDriver(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Nickname = r.GetString(1),
        Rank = Enum.TryParse<DriverRank>(r.GetString(2), out var rank) ? rank : DriverRank.Trainee,
        PermitAdr = r.GetBoolean(3),
        PermitHeavy = r.GetBoolean(4),
        PermitArticulated = r.GetBoolean(5),
        IsAdmin = r.GetBoolean(6),
        CreatedAt = r.GetDateTime(7),
    };

    public async Task<List<Driver>> GetDriversAsync()
    {
        await using var cmd = _source.CreateCommand($"SELECT {DriverColumns} FROM drivers ORDER BY id;");
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Driver>();
        while (await r.ReadAsync()) list.Add(ReadDriver(r));
        return list;
    }

    /// <summary>Находит водителя по никнейму и паролю; null — неверные данные.</summary>
    public async Task<Driver?> CheckPasswordAsync(string nickname, string password)
    {
        await using var cmd = _source.CreateCommand(
            $"SELECT {DriverColumns}, password_hash FROM drivers WHERE lower(nickname) = lower($1);");
        cmd.Parameters.AddWithValue(nickname.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return Pin.Verify(password, r.GetString(8)) ? ReadDriver(r) : null;
    }

    /// <summary>Регистрирует водителя по приглашению. Код действует один раз, даже при одновременных запросах.</summary>
    public async Task<(Driver? Driver, string? Error)> RegisterAsync(RegisterRequest req)
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();

        DriverRank rank;
        bool isAdmin;
        await using (var find = new NpgsqlCommand(
            "SELECT rank, is_admin FROM invites WHERE code = $1 AND used_at IS NULL FOR UPDATE;", c, tx))
        {
            find.Parameters.AddWithValue(req.Invite.Trim().ToUpperInvariant());
            await using var r = await find.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (null, "Неверный или уже использованный код приглашения.");
            rank = Enum.TryParse<DriverRank>(r.GetString(0), out var parsed) ? parsed : DriverRank.Trainee;
            isAdmin = r.GetBoolean(1);
        }

        // Ранг и допуски из заявки принимаем только у первого администратора; остальным их выдаёт администратор
        var driver = new Driver
        {
            Nickname = req.Nickname.Trim(),
            Rank = isAdmin ? req.Rank ?? rank : rank,
            PermitAdr = isAdmin && req.PermitAdr,
            PermitHeavy = isAdmin && req.PermitHeavy,
            PermitArticulated = isAdmin && req.PermitArticulated,
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO drivers (nickname, password_hash, rank, permit_adr, permit_heavy, permit_articulated, is_admin)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (lower(nickname)) DO NOTHING
            RETURNING id;
            """, c, tx))
        {
            insert.Parameters.AddWithValue(driver.Nickname);
            insert.Parameters.AddWithValue(Pin.Hash(req.Password));
            insert.Parameters.AddWithValue(driver.Rank.ToString());
            insert.Parameters.AddWithValue(driver.PermitAdr);
            insert.Parameters.AddWithValue(driver.PermitHeavy);
            insert.Parameters.AddWithValue(driver.PermitArticulated);
            insert.Parameters.AddWithValue(driver.IsAdmin);
            if (await insert.ExecuteScalarAsync() is not long id)
                return (null, $"Водитель «{driver.Nickname}» уже зарегистрирован.");
            driver.Id = id;
        }

        await using (var use = new NpgsqlCommand(
            "UPDATE invites SET used_by = $1, used_at = now() WHERE code = $2;", c, tx))
        {
            use.Parameters.AddWithValue(driver.Id);
            use.Parameters.AddWithValue(req.Invite.Trim().ToUpperInvariant());
            await use.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return (driver, null);
    }

    // ────────────────────────── Токены и приглашения ──────────────────────

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Выдаёт токен входа. В базе лежит только его хеш.</summary>
    public async Task<string> CreateTokenAsync(long driverId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await using var cmd = _source.CreateCommand("INSERT INTO tokens (token_hash, driver_id) VALUES ($1, $2);");
        cmd.Parameters.AddWithValue(HashToken(token));
        cmd.Parameters.AddWithValue(driverId);
        await cmd.ExecuteNonQueryAsync();
        return token;
    }

    public async Task<Driver?> GetDriverByTokenAsync(string token)
    {
        await using var cmd = _source.CreateCommand($"""
            SELECT {DriverColumns} FROM drivers
            WHERE id = (SELECT driver_id FROM tokens WHERE token_hash = $1);
            """);
        cmd.Parameters.AddWithValue(HashToken(token));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadDriver(r) : null;
    }

    public async Task DeleteTokenAsync(string token)
    {
        await using var cmd = _source.CreateCommand("DELETE FROM tokens WHERE token_hash = $1;");
        cmd.Parameters.AddWithValue(HashToken(token));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Создаёт одноразовый код приглашения: без похожих символов, чтобы его было легко переписать.</summary>
    public async Task<string> CreateInviteAsync(DriverRank rank, bool isAdmin, long? createdBy)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var code = new string(RandomNumberGenerator.GetItems<char>(alphabet, 10));
        await using var cmd = _source.CreateCommand(
            "INSERT INTO invites (code, rank, is_admin, created_by) VALUES ($1, $2, $3, $4);");
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(rank.ToString());
        cmd.Parameters.AddWithValue(isAdmin);
        cmd.Parameters.AddWithValue((object?)createdBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return code;
    }

    /// <summary>Неиспользованный код для первого администратора (пока водителей нет).</summary>
    public async Task<string?> FindBootstrapInviteAsync()
    {
        await using var cmd = _source.CreateCommand(
            "SELECT code FROM invites WHERE is_admin AND used_at IS NULL AND created_by IS NULL LIMIT 1;");
        return await cmd.ExecuteScalarAsync() as string;
    }

    // ───────────────────────────── Премии ─────────────────────────────

    public async Task<BonusEntry> AddBonusAsync(BonusRequest req, Driver admin)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO bonuses (driver_id, amount, reason, created_by) VALUES ($1, $2, $3, $4)
            RETURNING id, created_at;
            """);
        cmd.Parameters.AddWithValue(req.DriverId);
        cmd.Parameters.AddWithValue(req.Amount);
        cmd.Parameters.AddWithValue(req.Reason.Trim());
        cmd.Parameters.AddWithValue(admin.Id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new BonusEntry(r.GetInt64(0), req.DriverId, req.Amount, req.Reason.Trim(), r.GetDateTime(1), admin.Nickname);
    }

    /// <summary>Действующие премии (отменённые не входят); без номера водителя — все.</summary>
    public async Task<List<BonusEntry>> GetBonusesAsync(long? driverId = null)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT b.id, b.driver_id, b.amount, b.reason, b.created_at, COALESCE(a.nickname, '')
            FROM bonuses b LEFT JOIN drivers a ON a.id = b.created_by
            WHERE b.deleted_at IS NULL AND ($1::bigint IS NULL OR b.driver_id = $1)
            ORDER BY b.created_at DESC, b.id DESC;
            """);
        cmd.Parameters.Add(new NpgsqlParameter<long?> { TypedValue = driverId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BonusEntry>();
        while (await r.ReadAsync())
            list.Add(new BonusEntry(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetString(3), r.GetDateTime(4), r.GetString(5)));
        return list;
    }

    /// <summary>Отменяет премию. false — такой нет или она уже отменена.</summary>
    public async Task<bool> CancelBonusAsync(long id, long adminId)
    {
        await using var cmd = _source.CreateCommand(
            "UPDATE bonuses SET deleted_at = now(), deleted_by = $1 WHERE id = $2 AND deleted_at IS NULL;");
        cmd.Parameters.AddWithValue(adminId);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    // ────────────────────────────── Рейсы ──────────────────────────────

    public async Task<int> CountDeliveredBetweenAsync(long driverId, DateTime from, DateTime to)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COUNT(*) FROM trips
            WHERE driver_id = $1 AND status = 'Delivered' AND finished_at >= $2 AND finished_at < $3;
            """);
        cmd.Parameters.AddWithValue(driverId);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Сохраняет рейс. false — такой рейс этого водителя уже есть (повторная отправка).</summary>
    public async Task<bool> SaveTripAsync(TripResult t)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO trips (client_id, driver_id, status, kind, cargo_class, started_at, finished_at,
                cargo_id, cargo_name, cargo_mass_t, cargo_traits, special_job, from_place, to_place,
                from_city_id, to_city_id, truck, trailer_count, job_distance_km, driven_km, fuel_used_l,
                revenue, earned_xp, cargo_damage, truck_damage, trailer_damage, auto_parked,
                fines_count, fines_total, tolls_total, speeding_seconds, max_speed_kph, started_mid_job,
                score, grade, breakdown_json, salary, salary_json)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,
                $25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35,$36,$37,$38)
            ON CONFLICT (driver_id, client_id) DO NOTHING;
            """);
        var p = cmd.Parameters;
        p.AddWithValue(t.ClientId);
        p.AddWithValue(t.DriverId!.Value);
        p.AddWithValue(t.Status.ToString());
        p.AddWithValue(t.Kind.ToString());
        p.AddWithValue(t.CargoClass.ToString());
        p.AddWithValue(t.StartedAt);
        p.AddWithValue(t.FinishedAt);
        p.AddWithValue(t.CargoId);
        p.AddWithValue(t.CargoName);
        p.AddWithValue(t.CargoMassT);
        p.AddWithValue(t.Traits.ToString());
        p.AddWithValue(t.SpecialJob);
        p.AddWithValue(t.From);
        p.AddWithValue(t.To);
        p.AddWithValue(t.FromCityId);
        p.AddWithValue(t.ToCityId);
        p.AddWithValue(t.TruckName);
        p.AddWithValue(t.TrailerCount);
        p.AddWithValue(t.JobDistanceKm);
        p.AddWithValue(t.DrivenKm);
        p.AddWithValue(t.FuelUsedL);
        p.AddWithValue(t.Revenue);
        p.AddWithValue(t.EarnedXp);
        p.AddWithValue(t.CargoDamage);
        p.AddWithValue(t.TruckDamage);
        p.AddWithValue(t.TrailerDamage);
        p.AddWithValue(t.AutoParked);
        p.AddWithValue(t.FinesCount);
        p.AddWithValue(t.FinesTotal);
        p.AddWithValue(t.TollsTotal);
        p.AddWithValue(t.SpeedingSeconds);
        p.AddWithValue(t.MaxSpeedKph);
        p.AddWithValue(t.StartedMidJob);
        p.AddWithValue(t.Score);
        p.AddWithValue(t.Grade);
        p.AddWithValue(JsonSerializer.Serialize(t.Breakdown, ConfigStore.Json));
        p.AddWithValue(t.Salary);
        p.AddWithValue(JsonSerializer.Serialize(t.SalaryBreakdown, ConfigStore.Json));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Все рейсы компании — для рейтинга и достижений.</summary>
    public async Task<List<TripResult>> GetAllTripsAsync()
    {
        await using var cmd = _source.CreateCommand("""
            SELECT id, client_id, driver_id, status, kind, cargo_class, finished_at, cargo_mass_t, cargo_traits,
                special_job, trailer_count, job_distance_km, driven_km, fuel_used_l, revenue, cargo_damage,
                fines_count, speeding_seconds, score, grade, salary
            FROM trips ORDER BY finished_at, id;
            """);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<TripResult>();
        while (await r.ReadAsync())
        {
            list.Add(new TripResult
            {
                Id = r.GetInt64(0),
                ClientId = r.GetGuid(1),
                DriverId = r.GetInt64(2),
                Status = Enum.TryParse<TripStatus>(r.GetString(3), out var st) ? st : TripStatus.Delivered,
                Kind = Enum.TryParse<TripKind>(r.GetString(4), out var k) ? k : TripKind.Solo,
                CargoClass = Enum.TryParse<CargoClass>(r.GetString(5), out var cc) ? cc : CargoClass.Normal,
                FinishedAt = r.GetDateTime(6),
                CargoMassT = r.GetDouble(7),
                Traits = Enum.TryParse<CargoTraits>(r.GetString(8), out var tr) ? tr : CargoTraits.None,
                SpecialJob = r.GetBoolean(9),
                TrailerCount = r.GetInt32(10),
                JobDistanceKm = r.GetDouble(11),
                DrivenKm = r.GetDouble(12),
                FuelUsedL = r.GetDouble(13),
                Revenue = r.GetInt64(14),
                CargoDamage = r.GetDouble(15),
                FinesCount = r.GetInt32(16),
                SpeedingSeconds = r.GetDouble(17),
                Score = r.GetInt32(18),
                Grade = r.GetString(19),
                Salary = r.GetDouble(20),
            });
        }
        return list;
    }
}
