using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using EtsTracker;
using EtsTracker.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- Настройки компании: те же файлы, что и у трекера ---
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var scoring = ConfigStore.LoadOrCreate<ScoreSettings>(Path.Combine(dataDir, "scoring.json"));
var catalog = ConfigStore.LoadOrCreate<CargoCatalog>(Path.Combine(dataDir, "cargo.json"));
var company = ConfigStore.LoadOrCreate<CompanySettings>(Path.Combine(dataDir, "company.json"));
var score = new ScoreCalculator(scoring);
var pay = new PayCalculator(company);

var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION")
    ?? throw new InvalidOperationException("Не задана строка подключения DATABASE_CONNECTION.");
var db = new ServerDb(connectionString);
await db.InitAsync();

// Часовой пояс компании: границы суток, недели и месяца в рейтинге
var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("COMPANY_TZ") ?? "Europe/Moscow");

// Пока водителей нет, первый администратор регистрируется по коду из журнала сервера
if (await db.CountDriversAsync() == 0)
{
    var code = await db.FindBootstrapInviteAsync() ?? await db.CreateInviteAsync(DriverRank.Trainee, isAdmin: true, null);
    Console.WriteLine($"Код для первого администратора: {code}");
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = ApiJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.Encoder = ApiJson.Options.Encoder;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Подбор паролей и кодов: не больше 10 попыток в минуту с одного адреса
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// За обратным прокси (Caddy с HTTPS) настоящий адрес клиента приходит в заголовке,
// иначе ограничение попыток входа считалось бы одно на всех. Сервер в этом режиме закрыт снаружи
if (Environment.GetEnvironmentVariable("TRUST_PROXY") == "true")
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

static string? BearerToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
}

async Task<Driver?> CurrentDriver(HttpContext ctx)
    => BearerToken(ctx) is { Length: > 0 } token ? await db.GetDriverByTokenAsync(token) : null;

static ServerDriver ToApi(Driver d) => new(d.Id, d.Nickname, d.Rank, d.IsAdmin);

static IResult Error(string text, int status = StatusCodes.Status400BadRequest)
    => Results.Json(new ApiError(text), statusCode: status);

static IResult NotLoggedIn() => Error("Нужно войти заново.", StatusCodes.Status401Unauthorized);

// ─────────────────────────────── Вход ───────────────────────────────

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/register", async (RegisterRequest req) =>
{
    var nickname = req.Nickname?.Trim() ?? "";
    if (nickname.Length is < 2 or > 40) return Error("Никнейм — от 2 до 40 символов.");
    if ((req.Password?.Length ?? 0) < 8) return Error("Пароль — не короче 8 символов.");
    if (string.IsNullOrWhiteSpace(req.Invite)) return Error("Введите код приглашения.");

    var (driver, error) = await db.RegisterAsync(req);
    if (driver == null) return Error(error!);
    return Results.Ok(new AuthResponse(await db.CreateTokenAsync(driver.Id), ToApi(driver)));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginRequest req) =>
{
    var driver = await db.CheckPasswordAsync(req.Nickname ?? "", req.Password ?? "");
    if (driver == null) return Error("Неверный никнейм или пароль.", StatusCodes.Status401Unauthorized);
    return Results.Ok(new AuthResponse(await db.CreateTokenAsync(driver.Id), ToApi(driver)));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    if (BearerToken(ctx) is { Length: > 0 } token) await db.DeleteTokenAsync(token);
    return Results.NoContent();
});

app.MapGet("/api/me", async (HttpContext ctx) =>
    await CurrentDriver(ctx) is { } d ? Results.Ok(ToApi(d)) : NotLoggedIn());

app.MapPost("/api/invites", async (HttpContext ctx, InviteRequest req) =>
{
    var d = await CurrentDriver(ctx);
    if (d == null) return NotLoggedIn();
    if (!d.IsAdmin) return Error("Приглашения создаёт администратор.", StatusCodes.Status403Forbidden);
    return Results.Ok(new InviteResponse(await db.CreateInviteAsync(req.Rank, isAdmin: false, d.Id), req.Rank));
});

// ────────────────────────────── Рейсы ──────────────────────────────

app.MapPost("/api/trips", async (HttpContext ctx, List<TripUpload> uploads) =>
{
    var driver = await CurrentDriver(ctx);
    if (driver == null) return NotLoggedIn();
    if (uploads.Count > 100) return Error("За один раз — не больше 100 рейсов.");

    int accepted = 0, duplicates = 0;
    var saved = new List<Guid>();
    var rejected = new List<TripRejected>();

    // По порядку времени: от этого зависит надбавка за 5-й и следующие рейсы за сутки
    foreach (var upload in uploads.OrderBy(u => u.FinishedAt))
    {
        if (upload.Validate() is { } problem)
        {
            rejected.Add(new TripRejected(upload.ClientId, problem));
            continue;
        }

        // Трекеру верим только в измеренном: очки, класс груза и зарплату считаем здесь
        var trip = upload.ToTrip(driver.Id);
        trip.Traits = catalog.Classify(trip.CargoId, trip.CargoName);
        trip.CargoClass = company.DetectClass(trip);
        score.Apply(trip);

        var localDay = TimeZoneInfo.ConvertTime(upload.FinishedAt, timeZone);
        var dayStart = new DateTimeOffset(localDay.Date, localDay.Offset).UtcDateTime;
        int earlier = await db.CountDeliveredBetweenAsync(driver.Id, dayStart, trip.FinishedAt);
        pay.Apply(trip, driver, earlier);

        // Рейсы администратора принимаются сразу: не проверять же ему самого себя
        var approval = company.RequireTripApproval && !driver.IsAdmin ? TripApproval.Pending : TripApproval.Approved;
        if (await db.SaveTripAsync(trip, approval)) accepted++;
        else duplicates++;
        saved.Add(upload.ClientId);
    }
    return Results.Ok(new TripUploadResult(accepted, duplicates, saved, rejected));
});

// Свои рейсы: водитель видит, что принято, а что отклонено и почему
app.MapGet("/api/trips/mine", async (HttpContext ctx) =>
    await CurrentDriver(ctx) is { } d ? Results.Ok(await db.GetServerTripsAsync(null, d.Id, 100)) : NotLoggedIn());

app.MapGet("/api/trips", async (HttpContext ctx, TripApproval? approval) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Проверка рейсов — для администратора.", StatusCodes.Status403Forbidden);
    return Results.Ok(await db.GetServerTripsAsync(approval ?? TripApproval.Pending, null, 200));
});

app.MapPost("/api/trips/{id:long}/review", async (HttpContext ctx, long id, TripReview review) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Проверка рейсов — для администратора.", StatusCodes.Status403Forbidden);

    var note = review.Note?.Trim() ?? "";
    if (note.Length > 300) return Error("Комментарий — до 300 символов.");
    if (!review.Approve && note.Length == 0) return Error("Напишите, почему рейс отклонён.");

    var trip = await db.GetTripForPayAsync(id);
    if (trip == null) return Error("Рейс не найден.", StatusCodes.Status404NotFound);
    var driver = (await db.GetDriversAsync()).First(d => d.Id == trip.DriverId);

    trip.Kind = review.Kind ?? trip.Kind;
    trip.CargoClass = review.CargoClass ?? trip.CargoClass;

    // Зарплату пересчитываем всегда: мог поменяться класс груза или ранг водителя
    var localDay = TimeZoneInfo.ConvertTime(new DateTimeOffset(trip.FinishedAt), timeZone);
    var dayStart = new DateTimeOffset(localDay.Date, localDay.Offset).UtcDateTime;
    pay.Apply(trip, driver, await db.CountDeliveredBetweenAsync(driver.Id, dayStart, trip.FinishedAt));

    await db.SaveReviewAsync(trip, review.Approve ? TripApproval.Approved : TripApproval.Rejected, note, admin.Id);
    return Results.NoContent();
});

// ───────────────────────────── Водители ────────────────────────────

app.MapGet("/api/drivers", async (HttpContext ctx) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Только для администратора.", StatusCodes.Status403Forbidden);
    return Results.Ok((await db.GetDriversAsync()).Select(d => new ServerDriverInfo(
        d.Id, d.Nickname, d.Rank, Names.Of(d.Rank), d.PermitAdr, d.PermitHeavy, d.PermitArticulated, d.IsAdmin)));
});

app.MapPut("/api/drivers/{id:long}", async (HttpContext ctx, long id, DriverUpdate update) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Только для администратора.", StatusCodes.Status403Forbidden);

    var drivers = await db.GetDriversAsync();
    if (drivers.All(d => d.Id != id)) return Error("Водитель не найден.", StatusCodes.Status404NotFound);
    if (!update.IsAdmin && drivers.All(d => d.Id == id || !d.IsAdmin))
        return Error("Это единственный администратор. Сначала назначьте другого.");
    await db.UpdateDriverAsync(id, update);
    return Results.NoContent();
});

// ───────────────────────────── Премии ─────────────────────────────

app.MapPost("/api/bonuses", async (HttpContext ctx, BonusRequest req) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Премии начисляет администратор.", StatusCodes.Status403Forbidden);
    if (req.Amount is <= 0 or > 10_000_000) return Error("Сумма — от 0 до 10 000 000.");
    var reason = req.Reason?.Trim() ?? "";
    if (reason.Length is 0 or > 200) return Error("Укажите, за что премия (до 200 символов).");
    if ((await db.GetDriversAsync()).All(d => d.Id != req.DriverId)) return Error("Водитель не найден.", StatusCodes.Status404NotFound);
    return Results.Ok(await db.AddBonusAsync(req, admin));
});

// Причины премий видны только вошедшим: страница рейтинга открыта всем
app.MapGet("/api/drivers/{id:long}/bonuses", async (HttpContext ctx, long id) =>
    await CurrentDriver(ctx) == null ? NotLoggedIn() : Results.Ok(await db.GetBonusesAsync(id)));

app.MapDelete("/api/bonuses/{id:long}", async (HttpContext ctx, long id) =>
{
    var admin = await CurrentDriver(ctx);
    if (admin == null) return NotLoggedIn();
    if (!admin.IsAdmin) return Error("Премии отменяет администратор.", StatusCodes.Status403Forbidden);
    return await db.CancelBonusAsync(id, admin.Id) ? Results.NoContent() : Error("Премия не найдена.", StatusCodes.Status404NotFound);
});

// ──────────────────────────── Сейчас в рейсе ───────────────────────────

// Только в памяти: это мгновенное состояние, после перезапуска сервера трекеры пришлют его снова
var live = new ConcurrentDictionary<long, LiveTrip>();
var liveTimeout = TimeSpan.FromSeconds(60);

static string Short(string? text) => (text ?? "").Length > 80 ? text![..80] : text ?? "";

app.MapPost("/api/live", async (HttpContext ctx, LiveTripUpdate u) =>
{
    var d = await CurrentDriver(ctx);
    if (d == null) return NotLoggedIn();
    var clean = u with
    {
        CargoName = Short(u.CargoName),
        From = Short(u.From),
        To = Short(u.To),
        CargoMassT = Math.Clamp(u.CargoMassT, 0, 500),
        Progress = Math.Clamp(u.Progress, 0, 1),
        RemainingKm = Math.Clamp(u.RemainingKm, 0, 20_000),
        SpeedKph = Math.Clamp(u.SpeedKph, 0, 300),
    };
    live[d.Id] = new LiveTrip(d.Id, d.Nickname, Names.Of(d.Rank), clean, DateTime.UtcNow);
    return Results.NoContent();
});

app.MapDelete("/api/live", async (HttpContext ctx) =>
{
    var d = await CurrentDriver(ctx);
    if (d == null) return NotLoggedIn();
    live.TryRemove(d.Id, out _);
    return Results.NoContent();
});

app.MapGet("/api/live", () =>
{
    // Трекер закрылся или пропала связь: через минуту водитель сам исчезает из списка
    foreach (var (id, trip) in live)
        if (DateTime.UtcNow - trip.UpdatedAt > liveTimeout) live.TryRemove(id, out _);
    return Results.Ok(live.Values.OrderByDescending(t => t.Trip.Progress).ToList());
});

// ───────────────────────────── Рейтинг ─────────────────────────────

app.MapGet("/api/drivers/{id:long}/achievements", async (long id) =>
{
    var driver = (await db.GetDriversAsync()).FirstOrDefault(d => d.Id == id);
    if (driver == null) return Error("Водитель не найден.", StatusCodes.Status404NotFound);

    var trips = (await db.GetAllTripsAsync()).Where(t => t.DriverId == id);
    var items = Achievements.Evaluate(trips, scoring)
        .Select(a => new AchievementInfo(a.Def.Id, a.Def.Icon, a.Def.Title, a.Def.Description,
            a.Unlocked, a.UnlockedAt, Math.Min(a.Current, a.Def.Target), a.Def.Target, a.Def.Unit))
        .ToList();
    return Results.Ok(new DriverAchievements(id, driver.Nickname, items.Count(i => i.Unlocked), items.Count, items));
});

app.MapGet("/api/leaderboard", async (string? period, string? metric) =>
{
    period = period is "week" or "month" or "all" ? period : "month";
    metric = metric is "score" or "km" or "salary" or "bonus" or "delivered" ? metric : "score";

    var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    DateTime? since = period switch
    {
        "week" => new DateTimeOffset(now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)), now.Offset).UtcDateTime,
        "month" => new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset).UtcDateTime,
        _ => null,
    };

    var drivers = await db.GetDriversAsync();
    var allTrips = await db.GetAllTripsAsync();
    var byDriver = allTrips.ToLookup(t => t.DriverId);
    var allTime = DriverStats.Compute(allTrips);
    var bonuses = await db.GetBonusesAsync();
    var bonusByDriver = bonuses.Where(b => since == null || b.CreatedAt >= since).ToLookup(b => b.DriverId);

    var rows = drivers.Select(d =>
    {
        var all = byDriver[d.Id].ToList();
        var s = DriverStats.Compute(since == null ? all : all.Where(t => t.FinishedAt >= since));
        return new LeaderboardRow
        {
            DriverId = d.Id,
            Nickname = d.Nickname,
            Rank = d.Rank,
            RankName = Names.Of(d.Rank),
            IsAdmin = d.IsAdmin,
            Delivered = s.Delivered,
            Convoys = s.Convoys,
            Km = s.Km,
            Tonnes = s.Tonnes,
            Score = s.Score,
            Salary = s.Salary,
            Bonus = bonusByDriver[d.Id].Sum(b => b.Amount),
            Perfect = s.Perfect,
            Fines = s.Fines,
            Level = DriverLevel.From(all.Sum(t => (long)t.Score)).Level,
            Achievements = Achievements.Evaluate(all, scoring).Count(a => a.Unlocked),
        };
    });

    Func<LeaderboardRow, double> key = metric switch
    {
        "km" => r => r.Km,
        "salary" => r => r.Salary,
        "bonus" => r => r.Bonus,
        "delivered" => r => r.Delivered,
        _ => r => r.Score,
    };
    var sorted = rows.OrderByDescending(key).ThenByDescending(r => r.Score).ThenBy(r => r.Nickname)
        .Select((r, i) => r with { Place = i + 1 })
        .ToList();

    return Results.Ok(new Leaderboard
    {
        CompanyName = company.CompanyName,
        Period = period,
        Metric = metric,
        AchievementsTotal = Achievements.All.Count,
        CompanyAccount = allTime.Salary + bonuses.Sum(b => b.Amount) * company.CompanyAccountBonusShare
                         - allTime.FuelL * company.CompanyAccountFuelPerLiter,
        TotalKm = sorted.Sum(r => r.Km),
        TotalTonnes = sorted.Sum(r => r.Tonnes),
        TotalSalary = sorted.Sum(r => r.Salary),
        TotalBonus = sorted.Sum(r => r.Bonus),
        TotalDelivered = sorted.Sum(r => r.Delivered),
        Rows = sorted,
    });
});

app.Run();
