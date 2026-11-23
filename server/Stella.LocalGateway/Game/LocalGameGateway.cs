using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Proto;

/// <summary>
/// 游戏协议网关。每个 IKE 会话绑定一个账号 UID，登录后所有读写都只作用于该 UID 的存档。
///
/// 已实现的首批大厅链路：
///   1001 拉存档 / 10001 新角色通知
///   1004 创建角色 → 1002 完整 PlayerInfo（客户端回调契约，见 PROTOCOL_BACKLOG.md）
///   1015 引导进度上报 → 1016
///   1021 大厅展示角色保存 → 1022
///   4916 NPC 好感（星塔入口第一步）→ 4917
///   1301 星塔成长详情（入口第二步）→ 1302
/// </summary>
internal sealed partial class LocalGameGateway : IDisposable
{
    private const string SessionCookie = "stella_session";
    private const int MaxBoardIds = 8;
    private const int MaxNicknameRunes = 14;
    private const int MaxNicknameBytes = 21;

    private readonly MemoryCache sessions = new(new MemoryCacheOptions { SizeLimit = 128 });
    private readonly byte[] handshakeKey;
    private readonly LocalAccounts accounts;
    private readonly LocalPlayerStore playerStore;
    private readonly ILogger logger;
    private readonly ProtocolTrace trace;

    public LocalGameGateway(byte[] handshakeKey, LocalAccounts accounts, LocalPlayerStore playerStore,
        ILogger logger, ProtocolTrace trace)
    {
        this.handshakeKey = handshakeKey;
        this.accounts = accounts;
        this.playerStore = playerStore;
        this.logger = logger;
        this.trace = trace;
    }

    public IResult Handle(HttpContext context, byte[] frame)
    {
        short? requestId = null;
        try
        {
            // An IKE request can replace an expired or retained session cookie.
            (short Id, byte[] Body) handshake = default;
            try
            {
                handshake = GatewayFrame.ReadRequest(frame, handshakeKey, handshake: true);
            }
            catch (Exception error) when (error is CryptographicException or InvalidDataException)
            {
                // Most frames after IKE are authenticated by the per-session key below.
            }
            if (handshake.Body is not null)
            {
                requestId = handshake.Id;
                if (handshake.Id != 1)
                {
                    logger.LogWarning("Unexpected handshake protocol trace={Trace} id={Id} bytes={Bytes}",
                        context.TraceIdentifier, handshake.Id, handshake.Body.Length);
                    return Results.BadRequest();
                }
                var request = IKEReq.Parser.ParseFrom(handshake.Body);
                trace.Message(context, "IN", handshake.Id, request);
                return BeginSession(context, request);
            }
            var token = context.Request.Headers["X-Token"].ToString();
            if (token.Length == 0) token = context.Request.Cookies[SessionCookie];
            if (token is null || !sessions.TryGetValue<Session>(token, out var session) || session is null)
            {
                logger.LogWarning("Missing or expired game session trace={Trace} hasToken={HasToken}",
                    context.TraceIdentifier, token?.Length > 0);
                return Results.Unauthorized();
            }

            var (id, body) = GatewayFrame.ReadRequest(frame, session.Key, handshake: false);
            requestId = id;
            if (id == 4)
            {
                return HandleLogin(context, session, token, body);
            }
            if (!session.IsAuthenticated)
            {
                logger.LogWarning("Game request before login trace={Trace} id={Id}", context.TraceIdentifier, id);
                return Results.Unauthorized();
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            IMessage requestBody = id switch
            {
                1012 => Ping.Parser.ParseFrom(body),
                1004 => PlayerReg.Parser.ParseFrom(body),
                1006 => PlayerNameEditReq.Parser.ParseFrom(body),
                1009 => PlayerHeadIconSetReq.Parser.ParseFrom(body),
                1027 => PlayerSignatureEditReq.Parser.ParseFrom(body),
                1033 => PlayerCharsShowReq.Parser.ParseFrom(body),
                1036 => PlayerSkinShowReq.Parser.ParseFrom(body),
                1204 => FriendUIdSearchReq.Parser.ParseFrom(body),
                1207 => FriendNameSearchReq.Parser.ParseFrom(body),
                1210 => FriendAddReq.Parser.ParseFrom(body),
                1213 => FriendAddAgreeReq.Parser.ParseFrom(body),
                1219 => FriendDeleteReq.Parser.ParseFrom(body),
                1222 => FriendInvitesDeleteReq.Parser.ParseFrom(body),
                1225 => FriendStarSetReq.Parser.ParseFrom(body),
                1228 => FriendReceiveEnergyReq.Parser.ParseFrom(body),
                1231 => FriendSendEnergyReq.Parser.ParseFrom(body),
                9004 or 9007 or 9010 => MailRequest.Parser.ParseFrom(body),
                9013 => MailPinRequest.Parser.ParseFrom(body),
                1015 => NewbieInfo.Parser.ParseFrom(body),
                1021 => PlayerBoardSetReq.Parser.ParseFrom(body),
                5319 or 5401 => UI32.Parser.ParseFrom(body),
                _ => Nil.Parser.ParseFrom(body),
            };
            trace.Message(context, "IN", id, requestBody);
            switch (id)
            {
                case 1001:
                    return ReplyWithStoredPlayer(context, session, now);
                case 1004:
                    return CreatePlayer(context, session, (PlayerReg)requestBody);
                case 1006:
                    return EditName(context, session, (PlayerNameEditReq)requestBody);
                case 1009:
                    return EditHead(context, session, (PlayerHeadIconSetReq)requestBody);
                case 1027:
                    return EditSignature(context, session, (PlayerSignatureEditReq)requestBody);
                case 1033:
                    return EditShowCharacters(context, session, (PlayerCharsShowReq)requestBody);
                case 1036:
                    return EditShowSkin(context, session, (PlayerSkinShowReq)requestBody);
                case 1057:
                    var heads = new PlayerHeadIconInfoResp();
                    heads.List.Add(playerStore.ReadPlayer(session.Uid).Acc.HeadIcon);
                    return Reply(context, 1058, heads, session.Key);
                case 1201:
                    return Reply(context, 1202, playerStore.GetFriends(session.Uid), session.Key);
                case 1204:
                    var byId = new FriendUIdSearchResp();
                    byId.Friend = playerStore.SearchFriends(session.Uid, target: ((FriendUIdSearchReq)requestBody).Id).FirstOrDefault();
                    return Reply(context, 1205, byId, session.Key);
                case 1207:
                    var byName = new FriendNameSearchResp();
                    byName.Friends.AddRange(playerStore.SearchFriends(session.Uid, name: ((FriendNameSearchReq)requestBody).Name));
                    return Reply(context, 1208, byName, session.Key);
                case 1210:
                    return Reply(context, 1211, playerStore.AddFriend(session.Uid, ((FriendAddReq)requestBody).UId), session.Key);
                case 1213:
                    return Reply(context, 1214, playerStore.AgreeFriend(session.Uid, ((FriendAddAgreeReq)requestBody).UId), session.Key);
                case 1216:
                    return Reply(context, 1217, playerStore.AgreeAllFriends(session.Uid), session.Key);
                case 1219:
                    return Reply(context, 1220, playerStore.DeleteFriend(session.Uid, ((FriendDeleteReq)requestBody).UId), session.Key);
                case 1222:
                    return Reply(context, 1223, playerStore.DeleteFriendInvites(session.Uid, ((FriendInvitesDeleteReq)requestBody).UIds), session.Key);
                case 1225:
                    var star = (FriendStarSetReq)requestBody;
                    return Reply(context, 1226, playerStore.StarFriends(session.Uid, star.UIds, star.Star), session.Key);
                case 1228:
                    return Reply(context, 1229, playerStore.ReceiveFriendEnergy(session.Uid, ((FriendReceiveEnergyReq)requestBody).UIds), session.Key);
                case 1231:
                    return Reply(context, 1232, playerStore.SendFriendEnergy(session.Uid, ((FriendSendEnergyReq)requestBody).UIds), session.Key);
                case 1234:
                    var recommended = new FriendRecommendationGetResp();
                    recommended.Friends.AddRange(playerStore.SearchFriends(session.Uid, recommend: true));
                    return Reply(context, 1235, recommended, session.Key);
                case 9001:
                    return Reply(context, 9002, playerStore.ReadMails(session.Uid), session.Key);
                case 9004:
                    var read = (MailRequest)requestBody;
                    return Reply(context, 9005, playerStore.MutateMail(session.Uid, id, read.Id, read.Flag), session.Key);
                case 9007:
                    var recv = (MailRequest)requestBody;
                    return Reply(context, 9008, playerStore.MutateMail(session.Uid, id, recv.Id, recv.Flag), session.Key);
                case 9010:
                    var remove = (MailRequest)requestBody;
                    return Reply(context, 9011, playerStore.MutateMail(session.Uid, id, remove.Id, remove.Flag), session.Key);
                case 9013:
                    var pin = (MailPinRequest)requestBody;
                    return Reply(context, 9014, playerStore.MutateMail(session.Uid, id, pin.Id, pin.Flag, pin.Pin), session.Key);
                case 1012:
                    return Reply(context, 1013, new Pong { ServerTs = now }, session.Key);
                case 1015:
                    return LearnGuide(context, session, (NewbieInfo)requestBody);
                case 1021:
                    return SaveBoard(context, session, (PlayerBoardSetReq)requestBody);
                case 1301:
                    // 新账号没有任何已激活的成长节点，空列表就是真实初始状态。
                    return Reply(context, 1302, new TowerGrowthDetailResp(), session.Key);
                case 4722:
                    return Reply(context, 4723, new PotentialPreselectionList(), session.Key);
                case 4916:
                    // 本地存档没有 NPC 好感记录：0 条、计数 0，客户端按初始好感解析。
                    return Reply(context, 4917, new NPCAffinityBookGetResp { Number = 0 }, session.Key);
                case 5119:
                    return Reply(context, 5120, new MallPackageList(), session.Key);
                case 5319:
                    var season = (UI32)requestBody;
                    return Reply(context, 5320, new SoldierSeasonInfo { SeasonId = season.Value }, session.Key);
                case 5401:
                    return Reply(context, 5402, new Bytes(), session.Key);
                case 6004:
                    return Reply(context, 6005, new GachaInformationResp(), session.Key);
                case 6022:
                    return Reply(context, 6023, new GachaNewbieInfoResp(), session.Key);
                case 8419:
                    var guide = new GuideMissionInfo();
                    guide.Chapters.Add(new GuideMissionChapter { Id = 1, Done = true, Quests = new Quests() });
                    return Reply(context, 8420, guide, session.Key);
                case 9101:
                    return Reply(context, 9102, new ActivityResp(), session.Key);
                case 9201:
                    return Reply(context, 9202, new PhoneContactsInfoResp(), session.Key);
                case 11113:
                    // The client table for this build contains season 13.
                    return Reply(context, 11114, new ScoreBossInfo { ControlId = 13 }, session.Key);
                default:
                    logger.LogWarning("Unhandled game protocol trace={Trace} id={Id} bytes={Bytes}",
                        context.TraceIdentifier, id, body.Length);
                    return Results.BadRequest();
            }
        }
        catch (Exception error) when (error is GameRuleException or OverflowException or SqliteException)
        {
            logger.LogWarning(error, "Game operation failed trace={Trace} id={Id}", context.TraceIdentifier, requestId);
            var token = context.Request.Headers["X-Token"].ToString();
            if (token.Length == 0) token = context.Request.Cookies[SessionCookie] ?? "";
            if (!sessions.TryGetValue<Session>(token, out var session) || session is null) return Results.Unauthorized();
            return ReplyOperationFailure(context, session, requestId, error is GameRuleException ? error.Message : "操作失败，存档未提交");
        }
        catch (CryptographicException error)
        {
            logger.LogWarning(error, "Game frame cryptography failed trace={Trace} id={Id}", context.TraceIdentifier, requestId);
            return Results.Unauthorized();
        }
        catch (InvalidDataException error)
        {
            logger.LogWarning(error, "Invalid game frame trace={Trace} id={Id}", context.TraceIdentifier, requestId);
            return Results.BadRequest();
        }
        catch (InvalidProtocolBufferException)
        {
            logger.LogWarning("Invalid protobuf trace={Trace} id={Id}", context.TraceIdentifier, requestId);
            return Results.BadRequest();
        }
    }

    private IResult HandleLogin(HttpContext context, Session session, string sessionToken, byte[] body)
    {
        var login = LoginReq.Parser.ParseFrom(body);
        trace.Message(context, "IN", 4, login);
        ulong uid = 0;
        bool accepted = false;
        switch (login.AuthCase)
        {
            case LoginReq.AuthOneofCase.Account:
                accepted = accounts.Authenticate(login.Account.Name, login.Account.Pass, out uid);
                break;
            case LoginReq.AuthOneofCase.Official:
                accepted = accounts.VerifyTicket(login.Official.Uid, login.Official.Token, sessionToken);
                uid = login.Official.Uid;
                break;
        }

        if (!accepted || uid == 0)
        {
            logger.LogWarning("LoginReq rejected trace={Trace} case={Case}", context.TraceIdentifier, login.AuthCase);
            var error = new Error { Code = 100108 };
            error.Arguments.Add("本地账号凭据或登录票据无效");
            return Reply(context, 6, error, session.Key);
        }

        playerStore.TouchLogin(uid);
        session.MarkAuthenticated(uid);
        logger.LogInformation("Game login accepted trace={Trace} uid={Uid} hasCharacter={HasCharacter}",
            context.TraceIdentifier, uid, accounts.HasCharacter(uid));
        return Reply(context, 5, new LoginResp { Token = sessionToken }, session.Key);
    }

    /// <summary>
    /// 1001：有存档返回 1002；没有存档返回 10001 player_new_notify，
    /// 客户端据此打开创建角色界面（LoginCtrl:OnServerCallBack）。
    /// </summary>
    private IResult ReplyWithStoredPlayer(HttpContext context, Session session, long now)
    {
        if (!playerStore.PlayerExists(session.Uid))
        {
            logger.LogInformation("Player data requested before character creation trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            return Reply(context, 10001, new Nil(), session.Key);
        }

        PlayerInfo player;
        try
        {
            player = playerStore.ReadPlayer(session.Uid);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or InvalidProtocolBufferException)
        {
            logger.LogError(error, "Could not read player data trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            return Results.Problem(statusCode: 503, title: "Stored player data unavailable");
        }

        player.ServerTs = now;
        return Reply(context, 1002, player, session.Key);
    }

    /// <summary>
    /// 1004 创建角色。成功回包用 1002 PlayerInfo：
    /// 客户端没有 player_reg_succeed_ack，回调必须收到一个可解码的非 Error 包；
    /// CreatePlayerCtrl 回调后立即读取 PlayerData.Base._nPlayerId 并进塔，
    /// 只有 1002 会在同一包里缓存 UID、昵称、展示角色和引导进度。
    /// </summary>
    private IResult CreatePlayer(HttpContext context, Session session, PlayerReg request)
    {
        if (!TryNormalizeNickname(request.Nickname, out string nickname, out int errorCode, out string reason))
        {
            logger.LogInformation("player_reg rejected trace={Trace} uid={Uid} code={Code}",
                context.TraceIdentifier, session.Uid, errorCode);
            var error = new Error { Code = (uint)errorCode };
            error.Arguments.Add(reason);
            return Reply(context, 1005, error, session.Key);
        }

        PlayerInfo player;
        try
        {
            player = playerStore.CreatePlayer(session.Uid, nickname, request.Gender,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (PlayerAlreadyExistsException)
        {
            logger.LogWarning("player_reg for an existing character trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            var error = new Error { Code = 119909 };
            error.Arguments.Add("该账号已经创建过角色");
            return Reply(context, 1005, error, session.Key);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or InvalidProtocolBufferException)
        {
            logger.LogError(error, "Could not create character trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            var failure = new Error { Code = 110105 };
            failure.Arguments.Add("本地存档写入失败");
            return Reply(context, 1005, failure, session.Key);
        }

        logger.LogInformation("Character created trace={Trace} uid={Uid} nicknameLength={Length}",
            context.TraceIdentifier, session.Uid, nickname.Length);
        player.ServerTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Reply(context, 1002, player, session.Key);
    }

    /// <summary>1015 引导进度上报，写回 Acc.Newbies。</summary>
    private IResult LearnGuide(HttpContext context, Session session, NewbieInfo request)
    {
        if (request.GroupId == 0 || request.GroupId > 64 || request.StepId is < -1 or > 10_000)
        {
            var error = new Error { Code = 110104 };
            error.Arguments.Add("引导组或步骤超出范围");
            return Reply(context, 1017, error, session.Key);
        }

        try
        {
            playerStore.UpdatePlayer(session.Uid, player =>
            {
                player.Acc ??= new AccInfo();
                NewbieInfo? entry = player.Acc.Newbies.FirstOrDefault(item => item.GroupId == request.GroupId);
                if (entry is null)
                {
                    player.Acc.Newbies.Add(new NewbieInfo { GroupId = request.GroupId, StepId = request.StepId });
                }
                else
                {
                    entry.StepId = request.StepId;
                }
            });
            logger.LogInformation("Guide progress saved trace={Trace} uid={Uid} group={Group} step={Step}",
                context.TraceIdentifier, session.Uid, request.GroupId, request.StepId);
            return Reply(context, 1016, new Nil(), session.Key);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException)
        {
            logger.LogError(error, "Guide progress write failed trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            var failure = new Error { Code = 110105 };
            failure.Arguments.Add("本地存档写入失败");
            return Reply(context, 1017, failure, session.Key);
        }
    }

    /// <summary>1021 大厅展示角色保存，写回 PlayerInfo.Board。</summary>
    private IResult SaveBoard(HttpContext context, Session session, PlayerBoardSetReq request)
    {
        if (request.Ids.Count > MaxBoardIds || request.Ids.Any(id => id is 0 or > 100_000_000))
        {
            var error = new Error { Code = 110104 };
            error.Arguments.Add("展示角色列表无效");
            return Reply(context, 1023, error, session.Key);
        }

        try
        {
            playerStore.UpdatePlayer(session.Uid, player =>
            {
                player.Board.Clear();
                player.Board.AddRange(request.Ids.Distinct());
            });
            logger.LogInformation("Board saved trace={Trace} uid={Uid} count={Count}",
                context.TraceIdentifier, session.Uid, request.Ids.Count);
            return Reply(context, 1022, new Nil(), session.Key);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException)
        {
            logger.LogError(error, "Board write failed trace={Trace} uid={Uid}",
                context.TraceIdentifier, session.Uid);
            var failure = new Error { Code = 110105 };
            failure.Arguments.Add("本地存档写入失败");
            return Reply(context, 1023, failure, session.Key);
        }
    }

    /// <summary>
    /// 昵称规则对齐 CreatePlayerCtrl:CheckName：1-14 个 UTF-8 字符、最多 21 字节、
    /// 不能以数字开头、不能含控制字符。
    /// </summary>
    private static bool TryNormalizeNickname(string? raw, out string nickname, out int code, out string reason)
    {
        nickname = (raw ?? "").Trim();
        code = 0;
        reason = "";
        if (nickname.Length == 0)
        {
            code = 110201;
            reason = "昵称不能为空";
            return false;
        }

        int runes = 0;
        bool leadingDigit = false;
        bool invalid = false;
        foreach (Rune rune in nickname.EnumerateRunes())
        {
            if (runes == 0 && Rune.IsDigit(rune))
            {
                leadingDigit = true;
            }
            if (Rune.IsControl(rune) || rune.Value is 0xFFFD)
            {
                invalid = true;
            }
            runes++;
        }

        int bytes = Encoding.UTF8.GetByteCount(nickname);
        if (runes > MaxNicknameRunes || bytes > MaxNicknameBytes)
        {
            code = 110201;
            reason = $"昵称需为 1-{MaxNicknameRunes} 个字符";
            return false;
        }
        if (invalid || leadingDigit)
        {
            code = 110202;
            reason = leadingDigit ? "昵称不能以数字开头" : "昵称包含非法字符";
            return false;
        }
        return true;
    }

    private IResult BeginSession(HttpContext context, IKEReq request)
    {
        ReadOnlySpan<byte> clientPublic = request.PubKey.Span;
        if (request.ProtoVersion != 1 || clientPublic.Length != 65 || clientPublic[0] != 4)
        {
            return Reply(context, 3, new Error { Code = 100104 }, handshakeKey, handshake: true);
        }

        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = clientPublic[1..33].ToArray(), Y = clientPublic[33..].ToArray() },
        });
        using var local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECPoint point = local.ExportParameters(false).Q;
        byte[] localPublic = [4, .. point.X!, .. point.Y!];
        byte[] shared = local.DeriveRawSecretAgreement(peer.PublicKey);
        byte[] info = new byte[clientPublic.Length];
        for (int i = 0; i < info.Length; i++)
        {
            int publicByte = localPublic[i % localPublic.Length];
            info[i] = (byte)(clientPublic[i] ^ (clientPublic[i] > publicByte ? publicByte << 1 : publicByte >> 1));
        }
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, localPublic, info);
        CryptographicOperations.ZeroMemory(shared);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions.Set(token, new Session(key), new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(15),
        });
        context.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/agent-zone-1/",
        });
        return Reply(context, 2, new IKEResp
        {
            Token = token,
            Cipher = 1,
            PubKey = ByteString.CopyFrom(localPublic),
            ServerTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, handshakeKey, handshake: true);
    }

    private IResult Reply(HttpContext context, short id, IMessage message, byte[] key, bool handshake = false)
    {
        trace.Message(context, "OUT", id, message);
        return Results.Bytes(GatewayFrame.WriteResponse(id, message, key, handshake), "application/octet-stream");
    }

    public void Dispose() => sessions.Dispose();

    private sealed class Session(byte[] key)
    {
        private int authenticated;
        private long uid;

        public byte[] Key { get; } = key;
        public bool IsAuthenticated => Volatile.Read(ref authenticated) != 0;
        public ulong Uid => (ulong)Interlocked.Read(ref uid);

        public void MarkAuthenticated(ulong accountUid)
        {
            Interlocked.Exchange(ref uid, (long)accountUid);
            Volatile.Write(ref authenticated, 1);
        }
    }
}
