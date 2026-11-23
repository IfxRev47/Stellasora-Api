using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// 本地账号认证：登录、注册、SDK 会话票据。
///
/// 账号凭据只存在 SQLite 里（PBKDF2-SHA256，16 字节盐，210000 次迭代）。
/// 注册只建账号，不建角色；角色由游戏内的 player_reg(1004) 单独创建。
/// 短期票据绑定游戏会话；SDK 长期票据持久化哈希并允许同一 UID 重连。
/// </summary>
internal sealed partial class LocalAccounts : IDisposable
{
    private const int PasswordIterations = 210_000;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    /** SDK 会话链（quick-login -> detail -> LoginReq）跨多次请求，票据需要更长时效。 */
    private static readonly TimeSpan SessionTicketLifetime = TimeSpan.FromHours(12);
    private static readonly byte[] TimingSalt = RandomNumberGenerator.GetBytes(16);

    private readonly LocalPlayerStore store;
    private readonly string? nameOverride;
    private readonly string? passwordOverride;
    private readonly ulong testUid;
    private readonly string? testUsername;
    private readonly MemoryCache tickets = new(new MemoryCacheOptions { SizeLimit = 512 });
    private readonly ConcurrentDictionary<ulong, string> sessionTickets = new();
    private readonly object sessionGate = new();

    public LocalAccounts(LocalPlayerStore store, string? nameOverride, string? passwordOverride)
    {
        this.store = store;
        this.nameOverride = string.IsNullOrWhiteSpace(nameOverride) ? null : nameOverride.Trim();
        this.passwordOverride = string.IsNullOrEmpty(passwordOverride) ? null : passwordOverride;
        LocalAccountRecord? test = store.FindTestAccount();
        testUid = test?.Uid ?? 0;
        testUsername = test?.Username;
    }

    public IResult Login(LocalCredentials? credentials, HttpContext context, ProtocolTrace trace)
    {
        context.Response.Headers.CacheControl = "no-store";
        trace.Json(context, "IN", "LocalLogin", credentials);
        if (!IsValidUsername(credentials?.Username) || !IsValidPassword(credentials?.Password))
        {
            return BadRequest(context, trace, "LocalLoginError", "请输入 3-32 位字母、数字、下划线或短横线账号，以及 8-128 位密码");
        }

        if (!Authenticate(credentials!.Username, credentials.Password, out ulong uid))
        {
            return Fail(context, trace, "LocalLoginError", StatusCodes.Status401Unauthorized,
                "本地账号或密码不正确");
        }

        string token = "ym1." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets.Set(token, new Ticket(uid), new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TicketLifetime,
        });
        var reply = new
        {
            uid = uid.ToString(CultureInfo.InvariantCulture),
            token,
            expiresIn = (int)TicketLifetime.TotalSeconds,
            hasCharacter = store.PlayerExists(uid),
        };
        trace.Json(context, "OUT", "LocalLoginResult", reply);
        return Results.Ok(reply);
    }

    /// <summary>注册本地账号（只建账号，不建角色）。</summary>
    public IResult Register(LocalCredentials? credentials, HttpContext context, ProtocolTrace trace)
    {
        context.Response.Headers.CacheControl = "no-store";
        trace.Json(context, "IN", "LocalRegister", credentials);
        if (!IsValidUsername(credentials?.Username))
        {
            return BadRequest(context, trace, "LocalRegisterError", "账号需为 3-32 位字母、数字、下划线或短横线");
        }

        if (!IsValidPassword(credentials!.Password))
        {
            return BadRequest(context, trace, "LocalRegisterError", $"密码需为 {MinPasswordLength}-{MaxPasswordLength} 个字符");
        }

        if (IsReservedUsername(credentials.Username!))
        {
            return Fail(context, trace, "LocalRegisterError", StatusCodes.Status409Conflict, "该账号已存在，请直接登录");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = HashPassword(credentials.Password!, salt);
        try
        {
            LocalAccountRecord account = store.CreateAccount(
                credentials.Username!, salt, hash, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), credentials.RegistrationCode);
            var reply = new
            {
                uid = account.Uid.ToString(CultureInfo.InvariantCulture),
                username = account.Username,
                hasCharacter = false,
                next = "登录后需要在游戏内创建角色",
            };
            trace.Json(context, "OUT", "LocalRegisterResult", reply);
            return Results.Json(reply, statusCode: StatusCodes.Status201Created);
        }
        catch (GameRuleException error)
        {
            return BadRequest(context, trace, "LocalRegisterError", error.Message);
        }
        catch (DuplicateAccountException)
        {
            return Fail(context, trace, "LocalRegisterError", StatusCodes.Status409Conflict, "该账号已存在，请直接登录");
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            trace.Json(context, "OUT", "LocalRegisterError", new { error = error.SqliteErrorCode });
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "本地账号库不可用");
        }
    }

    /// <summary>账号密码校验。测试账号的运行时覆盖配置只对预置 UID 生效。</summary>
    public bool Authenticate(string? username, string? password, out ulong uid)
    {
        uid = 0;
        if (!IsValidUsername(username) || !IsValidPassword(password))
        {
            return false;
        }

        if (testUid != 0 && nameOverride == username && passwordOverride is not null && store.PasswordRevision(testUid) == 0)
        {
            // 配置了运行时覆盖时，预置测试账号只认覆盖密码（与原行为一致）。
            byte[] expected = Encoding.UTF8.GetBytes(passwordOverride);
            byte[] supplied = Encoding.UTF8.GetBytes(password!);
            uid = testUid;
            return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }

        LocalAccountRecord? account = store.FindAccount(nameOverride == username && testUsername is not null ? testUsername : username!);
        if (account is null)
        {
            // 账号不存在也做一次等价耗时的哈希，避免用响应时间枚举本地账号。
            HashPassword(password!, TimingSalt);
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(HashPassword(password!, account.PasswordSalt), account.PasswordHash))
        {
            return false;
        }

        uid = account.Uid;
        return true;
    }

    public bool VerifyTicket(ulong uid, string token, string sessionToken)
    {
        if (uid == 0 || string.IsNullOrEmpty(token)) return false;
        // SDK sessions survive reconnects/new IKE sessions; short login tickets remain single-session.
        if (store.ResolveSdkTicket(token) == uid) return true;
        return tickets.TryGetValue<Ticket>(token, out var ticket) && ticket is not null && ticket.Uid == uid && ticket.Bind(sessionToken);
    }

    /// <summary>票据对应哪个账号；不是本网关签发的返回 0。</summary>
    public ulong ResolveTicket(string? token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        if (tickets.TryGetValue<Ticket>(token, out var ticket) && ticket is not null) return ticket.Uid;
        return store.ResolveSdkTicket(token);
    }

    /// <summary>
    /// SDK 会话链（quick-login → detail → LoginReq）共用同一张长时效票据；
    /// 两个接口返回不同票据会让 SDK 缓存的 Token 互相覆盖。票据按账号区分。
    /// </summary>
    public string EnsureSessionTicket(ulong uid)
    {
        lock (sessionGate)
        {
            if (sessionTickets.TryGetValue(uid, out string? existing) &&
                store.ResolveSdkTicket(existing) == uid)
            {
                return existing;
            }

            string token = "ym1." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            store.SaveSdkTicket(uid, token, DateTimeOffset.UtcNow.Add(SessionTicketLifetime).ToUnixTimeSeconds());
            sessionTickets[uid] = token;
            return token;
        }
    }

    public bool HasCharacter(ulong uid) => store.PlayerExists(uid);

    /// <summary>预置测试账号 UID；没有测试账号时为 0。</summary>
    public ulong TestUid => testUid;

    public static bool IsValidUsername(string? username) =>
        username is { Length: >= 3 and <= 32 } && UsernamePattern().IsMatch(username);

    public static bool IsValidPassword(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength };

    private bool IsReservedUsername(string username) =>
        (testUsername is not null && string.Equals(testUsername, username, StringComparison.Ordinal)) ||
        (nameOverride is not null && string.Equals(nameOverride, username, StringComparison.Ordinal));

    public void Dispose() => tickets.Dispose();

    private static IResult BadRequest(HttpContext context, ProtocolTrace trace, string type, string message)
    {
        var error = new { error = message };
        trace.Json(context, "OUT", type, error);
        return Results.BadRequest(error);
    }

    private static IResult Fail(HttpContext context, ProtocolTrace trace, string type, int status, string message)
    {
        var error = new { error = message };
        trace.Json(context, "OUT", type, error);
        return Results.Json(error, statusCode: status);
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex UsernamePattern();

    private sealed class Ticket(ulong uid)
    {
        private string? session;

        public ulong Uid { get; } = uid;

        public bool Bind(string sessionToken)
        {
            var previous = Interlocked.CompareExchange(ref session, sessionToken, null);
            return previous is null || previous == sessionToken;
        }
    }
}

internal sealed record LocalCredentials(string? Username, string? Password, string? RegistrationCode = null);
