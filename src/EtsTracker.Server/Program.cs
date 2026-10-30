using System.Threading.RateLimiting;
using EtsTracker;
using EtsTracker.Server;
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

        if (await db.SaveTripAsync(trip)) accepted++;
        else duplicates++;
        saved.Add(upload.ClientId);
    }
    return Results.Ok(new TripUploadResult(accepted, duplicates, saved, rejected));
});

// ───────────────────────────── Рейтинг ─────────────────────────────

app.MapGet("/api/leaderboard", async (string? period, string? metric) =>
{
    period = period is "week" or "month" or "all" ? period : "month";
    metric = metric is "score" or "km" or "salary" or "delivered" ? metric : "score";

    var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    DateTime? since = period switch
    {
        "week" => new DateTimeOffset(now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)), now.Offset).UtcDateTime,
        "month" => new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset).UtcDateTime,
        _ => null,
    };

    var drivers = await db.GetDriversAsync();
    var byDriver = (await db.GetAllTripsAsync()).ToLookup(t => t.DriverId);

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
        TotalKm = sorted.Sum(r => r.Km),
        TotalTonnes = sorted.Sum(r => r.Tonnes),
        TotalSalary = sorted.Sum(r => r.Salary),
        TotalDelivered = sorted.Sum(r => r.Delivered),
        Rows = sorted,
    });
});

app.Run();
