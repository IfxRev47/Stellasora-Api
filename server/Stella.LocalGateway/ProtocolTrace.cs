using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

internal sealed class ProtocolTrace(bool enabled, ILogger logger)
{
    private const int MaxBodyChars = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly HashSet<string> RedactedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "pass", "password", "sessionKey", "handshakeKey", "serverMetaKey",
        "authorization", "cookie", "nextPackage",
    };

    public async Task TraceHttpAsync(HttpContext context, RequestDelegate next)
    {
        if (!enabled)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        var response = context.Response;
        var start = Stopwatch.GetTimestamp();
        logger.LogInformation("HTTP IN trace={Trace} {Method} {Path} bytes={Bytes}",
            context.TraceIdentifier, request.Method, request.Path, request.ContentLength);

        try
        {
            await next(context);
        }
        catch (Exception error)
        {
            logger.LogWarning("HTTP FAILED trace={Trace} {Method} {Path} error={Error}",
                context.TraceIdentifier, request.Method, request.Path, error.GetType().Name);
            throw;
        }

        logger.LogInformation("HTTP OUT trace={Trace} {Method} {Path} status={Status} bytes={Bytes} ms={ElapsedMs:F1}",
            context.TraceIdentifier, request.Method, request.Path, response.StatusCode,
            response.ContentLength, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public void Message(HttpContext context, string direction, short id, IMessage body)
    {
        if (!enabled) return;

        logger.LogInformation("PROTO {Direction} trace={Trace} id={Id} type={Type} plaintext={Plaintext}",
            direction, context.TraceIdentifier, id, body.Descriptor.FullName,
            FormatBody(JsonNode.Parse(JsonFormatter.Default.Format(body))));
    }

    public void Json<T>(HttpContext context, string direction, string type, T body)
    {
        if (!enabled) return;

        logger.LogInformation("BODY {Direction} trace={Trace} type={Type} plaintext={Plaintext}",
            direction, context.TraceIdentifier, type, FormatBody(JsonSerializer.SerializeToNode(body)));
    }

    private static string FormatBody(JsonNode? body)
    {
        Redact(body);
        var json = body?.ToJsonString(JsonOptions) ?? "null";
        return json.Length > MaxBodyChars ? json[..MaxBodyChars] + " [truncated]" : json;
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in obj.Select(field => field.Key).ToArray())
            {
                if (RedactedFields.Contains(name))
                {
                    obj[name] = "[redacted]";
                }
                else
                {
                    Redact(obj[name]);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Redact(item);
            }
        }
    }
}
