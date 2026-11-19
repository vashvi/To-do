using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;                              // NEW
 
var builder = WebApplication.CreateBuilder(args);
 
// ---------- Settings (see appsettings.json) ----------
var geo = builder.Configuration.GetSection("Geo");
var allowed = (geo.GetSection("AllowedCountries").Get<string[]>() ?? new[] { "IN", "AU" })
    .Select(c => c.Trim().ToUpperInvariant())
    .ToHashSet();
bool trustCloudflare = geo.GetValue<bool>("TrustCloudflareHeader");
bool behindProxy = geo.GetValue<bool>("BehindProxy");                  // NEW
bool allowTestHeader = geo.GetValue<bool>("AllowTestHeaderInDevelopment");
string mmdbSetting = geo.GetValue<string>("MaxMindDbPath") ?? "GeoLite2-Country.mmdb";
 
var app = builder.Build();
 
// ---------- GeoLite2 database (NEW) ----------
string mmdbPath = Path.IsPathRooted(mmdbSetting)
    ? mmdbSetting
    : Path.Combine(AppContext.BaseDirectory, mmdbSetting);
var geoLookup = new GeoLookup(mmdbPath, app.Logger);
 
if (!trustCloudflare && !geoLookup.IsReady)
    app.Logger.LogError(
        "GeoLite2 file not found at {File}. Without it every request is blocked.", mmdbPath);
 
// ---------- Real visitor IP when hosted behind a proxy (NEW) ----------
// Turn Geo:BehindProxy on ONLY if people can reach your server only through
// the host's proxy (this is normal for free cloud hosts).
if (behindProxy)
{
    var fwd = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor
    };
    fwd.KnownNetworks.Clear();
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}
 
static string? CleanCode(string? text)                                 // NEW
{
    var code = text?.Trim().ToUpperInvariant();
    return code is { Length: 2 } ? code : null;
}
 
// ---------- Country check: runs for EVERY request ----------
app.Use(async (ctx, next) =>
{
    string? country = null;
 
    // 1) Test helper. Works ONLY when running in Development.
    if (app.Environment.IsDevelopment() && allowTestHeader &&
        ctx.Request.Headers.TryGetValue("X-Test-Country", out var test))
    {
        country = CleanCode(test.ToString());
    }
 
    // 2) Country header added by Cloudflare (only if trustCloudflare is on).
    if (country is null && trustCloudflare &&
        ctx.Request.Headers.TryGetValue("CF-IPCountry", out var cf))
    {
        country = CleanCode(cf.ToString());
    }
 
    // 3) Our own lookup with the free MaxMind file.                   // CHANGED
    if (country is null)
    {
        IPAddress? ip = null;
 
        if (trustCloudflare &&
            IPAddress.TryParse(ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault(), out var cfIp))
        {
            ip = cfIp;
        }
 
        ip ??= ctx.Connection.RemoteIpAddress;
 
        if (ip is not null)
            country = geoLookup.GetCountry(ip);
    }
 
    ctx.Items["country"] = country;
 
    // Unknown country or not in the list -> blocked (fail closed)
    if (country is null || !allowed.Contains(country))
    {
        // NEW: log the country only (never the IP address)
        app.Logger.LogInformation("Blocked: country={Country} path={Path}",
            country ?? "unknown", ctx.Request.Path);
 
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "country_not_allowed", country });
        return;
    }
 
    await next();
});
 
// ---------- Simple storage in a JSON file (unchanged) ----------
var dataFile = Path.Combine(AppContext.BaseDirectory, "tasks.json");
var gate = new object();
List<TodoDto> tasks = new();
try
{
    if (File.Exists(dataFile))
        tasks = JsonSerializer.Deserialize<List<TodoDto>>(File.ReadAllText(dataFile)) ?? new();
}
catch { tasks = new(); }
 
// ---------- Endpoints (unchanged) ----------
app.MapGet("/api/health", (HttpContext c) =>
    Results.Ok(new { status = "ok", country = c.Items["country"] }));
 
app.MapGet("/api/tasks", () =>
{
    lock (gate) { return Results.Ok(tasks.ToList()); }
});
 
app.MapPut("/api/tasks", (List<TodoDto> incoming) =>
{
    var clean = incoming
        .Where(t => !string.IsNullOrWhiteSpace(t.Title))
        .Take(500)
        .Select(t =>
        {
            var title = t.Title.Trim();
            if (title.Length > 200) title = title[..200];
            return new TodoDto(title, t.IsDone);
        })
        .ToList();
 
    lock (gate)
    {
        tasks = clean;
        File.WriteAllText(dataFile, JsonSerializer.Serialize(tasks));
    }
    return Results.NoContent();
});
 
app.Run();
 
record TodoDto(string Title, bool IsDone);
