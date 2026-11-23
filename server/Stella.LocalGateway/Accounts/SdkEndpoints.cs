using System.Globalization;
using System.Text.Json;

/// <summary>
/// 悠星 SDK（com.airi.*）的本地实现。
///
/// 游戏登录走的是 SDK 自己的会话链：
///   LoginManager.sessionLogin(uid, token) -> POST /user/quick-login
///   -> POST /user/detail -> UnitySendMessage("TechSDKBridge","CallBack",OnLoginNotify)
/// 缺任何一个都会让 SDK 停在它自己的登录页并提示「网络异常」。
/// 这里按 Ba_Reverse/server/YostarMock 的响应结构补齐。
/// </summary>
internal static class SdkEndpoints
{
    /// <summary>
    /// SDK 的实体类（BaseResposeEntity / LoginEntity / UserDetailEntity）字段是 PascalCase，
    /// Gson 按字段名精确匹配；ASP.NET 默认输出 camelCase 会让 Code 恒为 0，直接走 onFail。
    /// </summary>
    private static readonly JsonSerializerOptions SdkJson = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = null,
    };

    public static void MapSdk(this IEndpointRouteBuilder endpoints, LocalAccounts accounts)
    {
        // 统一信封：{ Code, Msg, Data }
        static IResult Ok(object? data) =>
            Results.Json(new { Code = 200, Msg = "OK", Data = data ?? new { } }, SdkJson);

        IResult SdkLogin(HttpContext context)
        {
            // 票据以本地会话票据为准。只有「完全没带票据」的探测请求才退回预置测试账号；
            // 带了但校验不通过的票据一律拒绝，避免把 A 账号的会话识别成测试账号。
            string? presented = ReadSdkToken(context);
            ulong uid = accounts.ResolveTicket(presented);
            if (uid == 0 && string.IsNullOrEmpty(presented)) uid = accounts.TestUid;
            if (uid == 0) return Results.Json(new { Code = 401, Msg = "Invalid local ticket", Data = new { } }, SdkJson);
            return Ok(LoginPayload(uid, accounts.EnsureSessionTicket(uid), ReadPid(context)));
        }

        endpoints.MapPost("/user/login", SdkLogin);
        endpoints.MapPost("/user/quick-login", SdkLogin);
        endpoints.MapPost("/user/relink", SdkLogin);

        // SDK 会话链第二步：拉账号详情。
        // User.State 必须是 1，否则 SDK 会报 102403「账号已被封禁」；
        // 整个 Data 不能是空对象，否则解析直接失败。
        endpoints.MapPost("/user/detail", (HttpContext context) =>
        {
            string? presented = ReadSdkToken(context);
            ulong uid = accounts.ResolveTicket(presented);
            if (uid == 0 && string.IsNullOrEmpty(presented)) uid = accounts.TestUid;
            if (uid == 0) return Results.Json(new { Code = 401, Msg = "Invalid local ticket", Data = new { } }, SdkJson);
            string token = accounts.EnsureSessionTicket(uid);
            string uidText = uid.ToString(CultureInfo.InvariantCulture);
            string nick = "旅人" + uidText;
            return Ok(new
            {
                User = new
                {
                    ID = uid,
                    PID = ReadPid(context),
                    State = 1,
                    Token = token,
                    DestroyState = 0,
                },
                Pass = new
                {
                    ID = uid,
                    NickName = nick,
                    DefaultNickName = nick,
                    Picture = "",
                    State = 0,
                    CreatedAt = 0,
                },
                Identity = new { State = 1, Type = 1, Underage = false, RealName = "测试账号", IDCard = "", PI = "" },
                Keys = new[] { new { Type = "yostar", NickName = uidText } },
                IsTestAccount = true,
                Destroy = new { DestroyAt = 0 },
                PassDestroy = new { DestroyAt = 0 },
            });
        });

        // 其余 SDK 旁路接口：客户端只关心 Code==200，返回空对象即可。
        foreach (string path in new[]
                 {
                     "/user/send-sms", "/user/send-email", "/user/check-captcha", "/user/link",
                     "/user/relink-check", "/user/set-info", "/user/device/list", "/user/device/remove",
                     "/user/check-text", "/user/survey", "/user/destroy", "/user/cancel-destroy",
                     "/channel-user/destroy", "/pass/gen-token", "/order/create", "/order/detail",
                     "/common/geo-ip", "/common/agreement", "/common/client-code",
                     "/health-game/identity-auth", "/user/identity-auth/send", "/user/identity-auth/verify",
                 })
        {
            endpoints.MapPost(path, () => Ok(new { }));
        }

        endpoints.MapPost("/order/products", () => Ok(new { Product = Array.Empty<object>() }));
        endpoints.MapPost("/heartbeat/pulse", () => Ok(new { Delay = 300 }));
        endpoints.MapPost("/health-game/duration", () => Ok(new { Surplus = 9_999_999L }));
    }

    /// <summary>LoginEntity：SDK 用它拼出 OnLoginNotify 的 LOGIN_UID / LOGIN_TOKEN。</summary>
    private static object LoginPayload(ulong uid, string token, string pid) => new
    {
        IsNew = false,
        User = new
        {
            ID = uid,
            PID = pid,
            State = 0,
            Token = token,
        },
    };

    /// <summary>Authorization 可能是 "Bearer xxx"，也可能是 {"Head":{"Token":...,"PID":...}}。</summary>
    private static string? ReadSdkToken(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return authorization[7..].Trim();
        if (!authorization.StartsWith('{')) return null;
        try
        {
            using var json = JsonDocument.Parse(authorization);
            return TryHeadString(json.RootElement, "Token");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>PID 随客户端上报走，取不到时用官方安卓渠道的默认值。</summary>
    private static string ReadPid(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith('{')) return "CN-SS";
        try
        {
            using var json = JsonDocument.Parse(authorization);
            return TryHeadString(json.RootElement, "PID") ?? "CN-SS";
        }
        catch (JsonException)
        {
            return "CN-SS";
        }
    }

    private static string? TryHeadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Head", out var head) && head.ValueKind == JsonValueKind.Object &&
        head.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
