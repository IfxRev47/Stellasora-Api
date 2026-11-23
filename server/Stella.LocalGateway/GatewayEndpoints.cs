using System.Net;
using System.Text.Json;
using Google.Protobuf;

internal static class GatewayEndpoints
{
    public const int MaxFrameBytes = 64 * 1024;
    private const int MinFrameBytes = 12 + 2 + 16;

    public static void MapAccounts(this IEndpointRouteBuilder endpoints, LocalAccounts accounts, ProtocolTrace trace)
    {
        endpoints.MapGroup("/account-api")
            .MapPost("/login", (LocalCredentials? credentials, HttpContext context) => accounts.Login(credentials, context, trace))
            .WithName("LocalAccountLogin")
            .RequireRateLimiting("account-login");
        endpoints.MapGroup("/account-api")
            .MapPost("/register", (LocalCredentials? credentials, HttpContext context) => accounts.Register(credentials, context, trace))
            .WithName("LocalAccountRegister")
            .RequireRateLimiting("account-register");
    }

    public static void MapBootstrap(this IEndpointRouteBuilder endpoints, string fixtures, byte[] metadataKey, ProtocolTrace trace, LocalPlayerStore store)
    {
        var sdkConfig = File.ReadAllBytes(Path.Combine(fixtures, "common", "config"));
        var serverListPath = Path.Combine(fixtures, "meta", "serverlist.json");
        endpoints.MapGroup("/common").MapPost("/config", () => Results.Bytes(sdkConfig, "application/json; charset=utf-8"))
            .WithName("SdkConfig");
        endpoints.MapGroup("/meta").MapGet("/serverlist.html", async Task<IResult> (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (metadataKey.Length == 0)
            {
                trace.Json(context, "OUT", "ServerListError", new { error = "Configure Stella:ServerMetaKey" });
                return Results.Problem(statusCode: 503, title: "Configure Stella:ServerMetaKey");
            }

            await using var file = File.OpenRead(serverListPath);
            var serverList = await JsonSerializer.DeserializeAsync<ServerList>(file, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("serverlist.json must contain a server list object.");
            trace.Json(context, "OUT", "ServerList", serverList);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(serverList.Encrypt(metadataKey), "text/html");
        }).WithName("ServerList");

        endpoints.MapGroup("/notice").MapGet("/noticelist.html", (HttpContext context) =>
        {
            if (metadataKey.Length == 0)
            {
                return Results.Problem(statusCode: 503, title: "Configure Stella:ServerMetaKey");
            }

            var notices = new Pb.NoticeList();
            var active = store.ReadActiveNotices();
            foreach (string category in new[] { "system", "activity", "else_1", "else_2" })
            {
                var group = new Pb.NoticeType { Name = category };
                foreach (var notice in active.Where(x => x.Category == category))
                {
                    var item = new Pb.NoticeItem
                    {
                        Id = notice.Id, Title = notice.Title, Sort = notice.Sort,
                        EndTime = notice.EndAt == 0 ? 4102444800L : notice.EndAt,
                        ContentUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/notice/content/{notice.Id}.html?v={notice.UpdatedAt}",
                    };
                    item.Channel.Add(new uint[] { 1, 2, 4, 8, 16, 32, 128 });
                    group.Detail.Add(item);
                }
                notices.List.Add(group);
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(MetadataEnvelope.Encrypt(notices.ToByteArray(), metadataKey), "text/html");
        }).WithName("NoticeList");

        endpoints.MapGet("/notice/content/{id:long}.html", (long id, HttpContext context) =>
        {
            if (metadataKey.Length == 0) return Results.Problem(statusCode: 503, title: "Configure Stella:ServerMetaKey");
            var notice = store.ReadActiveNotices().FirstOrDefault(x => x.Id == (ulong)id);
            if (notice is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(MetadataEnvelope.Encrypt(notice.Html(), metadataKey), "application/octet-stream");
        }).WithName("NoticeContent");
    }

    public static void MapGame(this IEndpointRouteBuilder endpoints, LocalGameGateway? gameGateway, ILogger logger)
    {
        endpoints.MapPost("/agent-zone-1/", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            HttpRequest request = context.Request;
            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
                !string.Equals(contentType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            if (request.ContentLength > MaxFrameBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var frame = new MemoryStream();
            try
            {
                await request.Body.CopyToAsync(frame, cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (frame.Length < MinFrameBytes)
            {
                logger.LogWarning("Game request rejected trace={Trace}: incomplete frame, bytes={Bytes}",
                    context.TraceIdentifier, frame.Length);
                return Results.Problem(statusCode: 400, title: "Incomplete binary frame");
            }

            if (gameGateway is null)
            {
                logger.LogWarning("Game request rejected trace={Trace}: Configure Stella:HandshakeKey", context.TraceIdentifier);
                return Results.Problem(statusCode: 503, title: "Configure Stella:HandshakeKey");
            }
            return gameGateway.Handle(context, frame.ToArray());
        }).WithName("GameGateway");
    }

    public static void MapFixturePreview(this IEndpointRouteBuilder endpoints, LocalPlayerStore playerStore, ulong playerId)
    {
        endpoints.MapGet("/_dev/player-fixture", (HttpContext context) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                return Results.NotFound();
            }

            if (context.Request.Headers.Authorization != "Bearer stella-local-test")
            {
                return Results.Unauthorized();
            }

            return Results.Text(JsonFormatter.Default.Format(playerStore.ReadPlayer(playerId)), "application/json; charset=utf-8");
        });
    }
}
