using System.Net;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("Stella:Port", 1989);
var address = IPAddress.Parse(builder.Configuration["Stella:ListenAddress"] ?? "127.0.0.1");
var localSecrets = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).Build();
var configuredMetaKey = builder.Configuration["Stella:ServerMetaKey"] ?? localSecrets["Stella:ServerMetaKey"];
var metadataKey = Encoding.UTF8.GetBytes(configuredMetaKey ?? "");
if (metadataKey.Length is not (0 or 16))
{
    throw new InvalidOperationException("Stella:ServerMetaKey must be exactly 16 UTF-8 bytes.");
}
var handshakeKey = Encoding.UTF8.GetBytes(
    builder.Configuration["Stella:HandshakeKey"] ?? localSecrets["Stella:HandshakeKey"] ?? "");
if (handshakeKey.Length is not (0 or 32))
{
    throw new InvalidOperationException("Stella:HandshakeKey must be exactly 32 UTF-8 bytes.");
}
var localLogin = builder.Configuration["Stella:LocalLoginName"] ?? localSecrets["Stella:LocalLoginName"];
var localPassword = builder.Configuration["Stella:LocalLoginPassword"] ?? localSecrets["Stella:LocalLoginPassword"];

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
    if (!IPAddress.IsLoopback(address))
    {
        options.Listen(address, port);
    }
    options.Limits.MaxRequestBodySize = GatewayEndpoints.MaxFrameBytes;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, cancellationToken) => new ValueTask(
        context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "请求过于频繁，请稍后重试" }, cancellationToken));
    options.AddPolicy("account-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("account-register", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var trace = new ProtocolTrace(app.Configuration.GetValue<bool>("Stella:TraceProtocol"), app.Logger);
app.Use(trace.TraceHttpAsync);
app.UseRateLimiter();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var fixtures = Path.Combine(app.Environment.ContentRootPath, "Fixtures");
if (!Directory.Exists(fixtures))
{
    fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
}

var databasePath = Path.GetFullPath(app.Configuration["Stella:DatabasePath"] ?? "Data/stella.sqlite",
    app.Environment.ContentRootPath);
var playerStore = new LocalPlayerStore(databasePath);
var testAccount = playerStore.Initialize(Path.Combine(fixtures, "test"));
_ = playerStore.ReadPlayer(testAccount.Uid);
using var accounts = new LocalAccounts(playerStore, localLogin, localPassword);
using var gameGateway = handshakeKey.Length == 32
    ? new LocalGameGateway(handshakeKey, accounts, playerStore, app.Logger, trace)
    : null;

app.MapAccounts(accounts, trace);
app.MapSdk(accounts);
app.MapBootstrap(fixtures, metadataKey, trace, playerStore);
app.MapGame(gameGateway, app.Logger);
if (app.Configuration.GetValue<bool>("Stella:EnableLocalFixture"))
{
    app.MapFixturePreview(playerStore, testAccount.Uid);
}
app.Run();
