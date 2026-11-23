using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Proto;

// 独立测试客户端：自己实现握手/会话帧和 HTTP，不复用网关的 GatewayFrame。
// 用法：
//   STELLA_BASE_URL=http://127.0.0.1:1989 STELLA_HANDSHAKE_KEY=<32字节> dotnet run -- full
//   ... dotnet run -- verify     # 重启服务端后校验写操作是否持久化
var baseUrl = (Environment.GetEnvironmentVariable("STELLA_BASE_URL") ?? "http://127.0.0.1:1989").TrimEnd('/');
var handshakeKey = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("STELLA_HANDSHAKE_KEY") ?? "");
if (handshakeKey.Length != 32)
{
    Console.Error.WriteLine("STELLA_HANDSHAKE_KEY 必须是 32 个 UTF-8 字节");
    return 2;
}

var mode = args.Length > 0 ? args[0] : "full";
string statePath = Path.Combine(AppContext.BaseDirectory, "probe-state.json");
var probe = new Probe(baseUrl, handshakeKey);
try
{
    return mode switch
    {
        "full" => await probe.RunFullAsync(statePath),
        "verify" => await probe.VerifyPersistedAsync(statePath),
        "race" => await probe.RunRaceAsync(),
        "gm" => await probe.RunGmAsync(),
        "friends" => await probe.RunFriendsAsync(statePath),
        "friends-verify" => await probe.VerifyFriendsAsync(statePath),
        _ => throw new ArgumentException($"未知模式：{mode}"),
    };
}
finally
{
    probe.Dispose();
}

internal sealed class Probe(string baseUrl, byte[] handshakeKey) : IDisposable
{
    private const string TestAccount = "stella-test";
    private const string TestPassword = "stella-local-10001";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private int failures;

    public async Task<int> RunFriendsAsync(string statePath)
    {
        string suffix=DateTime.UtcNow.ToString("MMddHHmmssfff");
        var users=new List<(string Name,ulong Uid,string Token)>();
        foreach(string label in new[]{"a","b","c"}) {
            string name=$"friend_{label}_{suffix}";
            var (status,json)=await PostJsonAsync("/account-api/register",new { username=name,password="friend-test-pass" });
            Check("创建独立好友账号 "+label,status==201);
            ulong uid=ulong.Parse(Text(json,"uid"));
            (status,json)=await PostJsonAsync("/account-api/login",new {username=name,password="friend-test-pass"});
            string shortToken=Text(json,"token");
            using var sdkRequest=new HttpRequestMessage(HttpMethod.Post,baseUrl+"/user/quick-login");
            sdkRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",shortToken);
            sdkRequest.Content=new StringContent("{}",Encoding.UTF8,"application/json");
            using var response=await http.SendAsync(sdkRequest);
            using var sdkJson=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string token=sdkJson.RootElement.GetProperty("Data").GetProperty("User").GetProperty("Token").GetString()!;
            users.Add((name,uid,token));
        }
        var a=users[0];var b=users[1];var c=users[2];
        async Task<GameClient> Connect((string Name,ulong Uid,string Token) user) {
            var client=new GameClient(baseUrl,handshakeKey);await client.ConnectAsync();
            var reply=await client.SendAsync(4,new LoginReq {Official=new Official {Uid=user.Uid,Token=user.Token}});
            Check("SDK 登录/重登允许新 IKE 会话",reply.Id==5);return client;
        }
        using var ca=await Connect(a);using var cb=await Connect(b);using var cc=await Connect(c);
        foreach(var pair in new[]{(ca,"好友甲"),(cb,"好友乙"),(cc,"好友丙")})
            Check("创建好友角色",(await pair.Item1.SendAsync(1004,new PlayerReg {Nickname=pair.Item2})).Id==1002);
        using var reconnect=await Connect(a);
        var wrong=await reconnect.SendAsync(4,new LoginReq {Official=new Official {Uid=b.Uid,Token=a.Token}});
        Check("SDK 票据不能冒用其他 UID",wrong.Id==6);
        wrong=await reconnect.SendAsync(4,new LoginReq {Official=new Official {Uid=a.Uid,Token="unknown-token"}});
        Check("未知票据仍拒绝",wrong.Id==6);
        var reply=await ca.SendAsync(1201,new Nil());
        Check("1201 返回可解码的空列表",reply.Id==1202 && FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Count==0);
        reply=await ca.SendAsync(1204,new FriendUIdSearchReq {Id=b.Uid});
        var profile=FriendUIdSearchResp.Parser.ParseFrom(reply.Body).Friend;
        Check("UID 查询真实存档资料和纳秒登录时间",reply.Id==1205 && profile.Id==b.Uid && profile.NickName=="好友乙" && profile.CharShows.Count==3 && profile.LastLoginTime>1_000_000_000_000_000_000);
        Check("好友列表携带客户端配置的默认称号",profile.TitlePrefix==1 && profile.TitleSuffix==2);
        reply=await ca.SendAsync(1207,new FriendNameSearchReq {Name="好友乙"});
        Check("昵称搜索",reply.Id==1208 && FriendNameSearchResp.Parser.ParseFrom(reply.Body).Friends.Any(x=>x.Id==b.Uid));
        reply=await ca.SendAsync(1204,new FriendUIdSearchReq {Id=999999999});
        Check("查无 UID 返回空结果",reply.Id==1205 && FriendUIdSearchResp.Parser.ParseFrom(reply.Body).Friend is null);
        reply=await ca.SendAsync(1210,new FriendAddReq {UId=a.Uid});Check("不能添加自己",reply.Id==1212);
        reply=await ca.SendAsync(1210,new FriendAddReq {UId=b.Uid});Check("发送好友申请",reply.Id==1211);
        await ca.SendAsync(1210,new FriendAddReq {UId=b.Uid});
        reply=await cb.SendAsync(1201,new Nil());Check("申请不重复且只出现在接收方",FriendListGetResp.Parser.ParseFrom(reply.Body).Invites.Count(x=>x.Id==a.Uid)==1);
        reply=await cc.SendAsync(1213,new FriendAddAgreeReq {UId=a.Uid});Check("第三个账号不能代同意申请",reply.Id==1215);
        reply=await cb.SendAsync(1213,new FriendAddAgreeReq {UId=a.Uid});Check("同意好友申请",reply.Id==1214 && FriendAddAgreeResp.Parser.ParseFrom(reply.Body).Friend.Id==a.Uid);
        reply=await ca.SendAsync(1201,new Nil());Check("同意后申请方也有双向关系",FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Single().Base.Id==b.Uid);
        reply=await cb.SendAsync(1201,new Nil());Check("接收方清除已处理申请",FriendListGetResp.Parser.ParseFrom(reply.Body).Invites.Count==0 && FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Single().Base.Id==a.Uid);
        reply=await ca.SendAsync(1225,new FriendStarSetReq {UIds={b.Uid},Star=true});Check("星标好友",reply.Id==1226);
        reply=await cb.SendAsync(1201,new Nil());Check("星标不会修改对方的设置",!FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Single().Star);
        reply=await ca.SendAsync(1231,new FriendSendEnergyReq {UIds={b.Uid,b.Uid}});Check("去重赠送体力",reply.Id==1232 && FriendSendEnergyResp.Parser.ParseFrom(reply.Body).UIds.Count==1);
        await ca.SendAsync(1231,new FriendSendEnergyReq {UIds={b.Uid}});
        reply=await cb.SendAsync(1201,new Nil());Check("接收方有一份可领取体力",FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Single().GetEnergy==1);
        using var cb2=await Connect(b);
        var claims=await Task.WhenAll(cb.SendAsync(1228,new FriendReceiveEnergyReq {UIds={a.Uid}}),cb2.SendAsync(1228,new FriendReceiveEnergyReq {UIds={a.Uid}}));
        Check("并发领取只发一份体力",claims.All(x=>x.Id==1229) && claims.Sum(x=>FriendReceiveEnergyResp.Parser.ParseFrom(x.Body).UIds.Count)==1);
        var claim=claims.Select(x=>FriendReceiveEnergyResp.Parser.ParseFrom(x.Body)).Single(x=>x.UIds.Count==1);
        Check("体力回包携带真实 Energy 快照",claim.Change.Props.Single().Unpack<Energy>().Primary==101 && claim.ReceiveEnergyCnt==1);
        reply=await cb.SendAsync(1001,new Nil());Check("体力入账持久化",PlayerInfo.Parser.ParseFrom(reply.Body).Energy.Energy.Primary==101);
        await cc.SendAsync(1210,new FriendAddReq {UId=a.Uid});
        reply=await ca.SendAsync(1222,new FriendInvitesDeleteReq {UIds={c.Uid}});Check("拒绝申请",reply.Id==1223);
        await cc.SendAsync(1210,new FriendAddReq {UId=a.Uid});
        reply=await ca.SendAsync(1216,new Nil());Check("全部同意返回完整好友列表",reply.Id==1217 && FriendAllAgreeResp.Parser.ParseFrom(reply.Body).Friends.Count==2);
        reply=await ca.SendAsync(1234,new Nil());Check("推荐排除自己与已有好友",reply.Id==1235 && FriendRecommendationGetResp.Parser.ParseFrom(reply.Body).Friends.All(x=>x.Id!=a.Uid && x.Id!=b.Uid && x.Id!=c.Uid));
        reply=await ca.SendAsync(1219,new FriendDeleteReq {UId=c.Uid});Check("删除好友",reply.Id==1220);
        reply=await cc.SendAsync(1201,new Nil());Check("删除为双向解除",FriendListGetResp.Parser.ParseFrom(reply.Body).Friends.Count==0);
        reply=await cc.SendAsync(1231,new FriendSendEnergyReq {UIds={a.Uid}});Check("非好友不能赠送体力",reply.Id==1233);
        await File.WriteAllTextAsync(statePath+".friends",JsonSerializer.Serialize(new {aName=a.Name,aUid=a.Uid,aToken=a.Token,bName=b.Name,bUid=b.Uid,bToken=b.Token}));
        Console.WriteLine($"Friend integration failures={failures}");return failures==0?0:1;
    }

    public async Task<int> VerifyFriendsAsync(string statePath)
    {
        using var state=JsonDocument.Parse(await File.ReadAllTextAsync(statePath+".friends"));var data=state.RootElement;
        using var ca=new GameClient(baseUrl,handshakeKey);await ca.ConnectAsync();
        var reply=await ca.SendAsync(4,new LoginReq {Official=new Official {Uid=data.GetProperty("aUid").GetUInt64(),Token=data.GetProperty("aToken").GetString()!}});
        Check("服务重启后 SDK 长期票据仍可重登",reply.Id==5);
        reply=await ca.SendAsync(1201,new Nil());var list=FriendListGetResp.Parser.ParseFrom(reply.Body);
        Check("重启后好友关系与星标仍存在",reply.Id==1202 && list.Friends.Single().Star && list.Friends.Single().SendEnergy);
        using var cb=new GameClient(baseUrl,handshakeKey);await cb.ConnectAsync();
        reply=await cb.SendAsync(4,new LoginReq {Official=new Official {Uid=data.GetProperty("bUid").GetUInt64(),Token=data.GetProperty("bToken").GetString()!}});
        Check("另一账号 SDK 票据独立持久化",reply.Id==5);
        reply=await cb.SendAsync(1228,new FriendReceiveEnergyReq {UIds={data.GetProperty("aUid").GetUInt64()}});
        Check("重启后不能重复领取体力",reply.Id==1229 && FriendReceiveEnergyResp.Parser.ParseFrom(reply.Body).UIds.Count==0);
        return failures==0?0:1;
    }

    public async Task<int> RunGmAsync()
    {
        string gmUrl = Environment.GetEnvironmentVariable("STELLA_GM_URL") ?? "http://127.0.0.1:8789";
        async Task<(int Status, JsonElement Json)> Gm(string path, object? data = null, string method = "POST")
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), gmUrl + "/api" + path);
            if (data is not null) request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ((int)response.StatusCode, json.RootElement.Clone());
        }
        string suffix = DateTime.UtcNow.ToString("MMddHHmmssfff");
        // This mode is for an isolated database; it deliberately changes the fixture password.
        var (legacyStatus, legacyJson) = await PostJsonAsync("/account-api/login", new { username = "reset-test-alias", password = "runtime-override-123" });
        Check("测试账号运行时覆盖密码生效", legacyStatus == 200);
        await Gm("/accounts/10001/password", new { password = "db-reset-password-123" }, "PATCH");
        (legacyStatus, legacyJson) = await PostJsonAsync("/account-api/login", new { username = "reset-test-alias", password = "db-reset-password-123" });
        Check("重置后测试账号别名使用数据库新密码", legacyStatus == 200);
        (legacyStatus, legacyJson) = await PostJsonAsync("/account-api/login", new { username = "reset-test-alias", password = "runtime-override-123" });
        Check("重置后覆盖旧密码失效", legacyStatus == 401);
        using var legacyClient = new GameClient(baseUrl, handshakeKey);
        await legacyClient.ConnectAsync(); await legacyClient.LoginAsync(TestAccount, "db-reset-password-123");
        var legacyReply = await legacyClient.SendAsync(1001, new Nil());
        Check("兼容旧存档缺少 SkinId", legacyReply.Id == 1002 && PlayerInfo.Parser.ParseFrom(legacyReply.Body).Acc.SkinId == 10301);
        var (status, json) = await Gm("/accounts", new { username = "gm_" + suffix, password = "before-reset-123", nickname = "管理探针" });
        Check("GM 创建含角色账号", status == 201);
        string uid = Text(json, "uid");
        (status, json) = await Gm($"/accounts/{uid}/password", new { password = "after-reset-456" }, "PATCH");
        Check("密码重置有明确响应", status == 200 && json.GetProperty("ok").GetBoolean());
        (status, json) = await PostJsonAsync("/account-api/login", new { username = "gm_" + suffix, password = "before-reset-123" });
        Check("旧密码失效", status == 401);
        (status, json) = await PostJsonAsync("/account-api/login", new { username = "gm_" + suffix, password = "after-reset-456" });
        Check("新密码通过实际网关", status == 200);
        using var client = new GameClient(baseUrl, handshakeKey);
        await client.ConnectAsync();
        var reply = await client.LoginAsync("gm_" + suffix, "after-reset-456");
        Check("新密码通过游戏会话登录", reply.Id == 5);
        reply = await client.SendAsync(1001, new Nil());
        var before = PlayerInfo.Parser.ParseFrom(reply.Body);
        Check("个人信息展示皮肤非 0", before.Acc.SkinId == 10301);
        reply = await client.SendAsync(1036, new PlayerSkinShowReq { SkinId = 11101 });
        Check("修改已拥有展示皮肤", reply.Id == 1037);
        reply = await client.SendAsync(1036, new PlayerSkinShowReq { SkinId = 99999999 });
        Check("拒绝无效皮肤并回协议失败号", reply.Id == 1038);
        reply = await client.SendAsync(1006, new PlayerNameEditReq { Name = "资料探针" });
        Check("游戏内改名", reply.Id == 1007);
        reply = await client.SendAsync(1027, new PlayerSignatureEditReq { Signature = "验证签名" });
        Check("游戏内改签名", reply.Id == 1028);
        reply = await client.SendAsync(1033, new PlayerCharsShowReq { CharIds = { 111, 103 } });
        Check("游戏内修改展示成员", reply.Id == 1034);
        reply = await client.SendAsync(1057, new Nil());
        Check("头像列表可解码", reply.Id == 1058 && PlayerHeadIconInfoResp.Parser.ParseFrom(reply.Body).List.Contains(101));
        reply = await client.SendAsync(1009, new PlayerHeadIconSetReq { HeadIcon = 101 });
        Check("头像保存", reply.Id == 1010);
        (status, json) = await Gm("/catalog/items?q=" + Uri.EscapeDataString("朵拉"), method: "GET");
        Check("中文物品查询", status == 200 && json.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetUInt32() == 1));
        var attachments = new[] { new { tid = 1, qty = 25 }, new { tid = 30001, qty = 2 } };
        (status, json) = await Gm($"/accounts/{uid}/mails", new { subject = "测试附件", desc = "邮件端到端验证", days = 30, attachments });
        Check("GM 下发邮件", status == 201);
        uint mailId = json.GetProperty("id").GetUInt32();
        reply = await client.SendAsync(9001, new Nil());
        var mail = Mails.Parser.ParseFrom(reply.Body).List.Single(x => x.Id == mailId);
        Check("游戏内邮件列表读取附件", reply.Id == 9002 && mail.Attachments.Count == 2);
        reply = await client.SendAsync(9004, new MailRequest { Id = mail.Id, Flag = mail.Flag });
        Check("邮件已读", reply.Id == 9005);
        reply = await client.SendAsync(9013, new MailPinRequest { Id = mail.Id, Flag = mail.Flag, Pin = true });
        Check("邮件置顶", reply.Id == 9014);
        reply = await client.SendAsync(9010, new MailRequest { Id = mail.Id, Flag = mail.Flag });
        Check("未领奖置顶邮件不可删除", reply.Id == 9012);
        using var other = new GameClient(baseUrl, handshakeKey);
        await other.ConnectAsync(); await other.LoginAsync("gm_" + suffix, "after-reset-456");
        var results = await Task.WhenAll(client.SendAsync(9007, new MailRequest { Id = mail.Id, Flag = mail.Flag }),
            other.SendAsync(9007, new MailRequest { Id = mail.Id, Flag = mail.Flag }));
        Check("并发领取只发奖一次", results.All(x => x.Id == 9008) && results.Sum(x => MailRecvResp.Parser.ParseFrom(x.Body).Ids.Count) == 1);
        var change = results.Select(x => MailRecvResp.Parser.ParseFrom(x.Body)).Single(x => x.Ids.Count == 1).Items;
        Check("ChangeInfo 携带资源与物品增量", change.Props.Count == 2 && change.Props[0].Unpack<Res>().Qty == 25 && change.Props[1].Unpack<Item>().Qty == 2);
        reply = await client.SendAsync(1001, new Nil());
        var after = PlayerInfo.Parser.ParseFrom(reply.Body);
        Check("附件和资料已持久化", after.Acc.SkinId == 11101 && after.Acc.NickName == "资料探针" && after.Acc.Signature == "验证签名" &&
            after.Res.Single(x => x.Tid == 1).Qty == 25 && after.Items.Single(x => x.Tid == 30001).Qty == 2);
        reply = await client.SendAsync(9013, new MailPinRequest { Id = mail.Id, Flag = mail.Flag, Pin = false });
        reply = await client.SendAsync(9010, new MailRequest { Id = mail.Id, Flag = mail.Flag });
        Check("领取后取消置顶可删除", reply.Id == 9011);

        (status, json) = await Gm("/registration/settings", new { requireCode = true }, "PUT");
        Check("开启注册码注册", status == 200);
        (status, json) = await Gm("/register", new { username = "no_code_" + suffix, password = "register-pass-123" });
        Check("GM 玩家注册无注册码拒绝", status == 400);
        (status, json) = await PostJsonAsync("/account-api/register", new { username = "no_code2_" + suffix, password = "register-pass-123" });
        Check("网关玩家注册同样拒绝无码", status == 400);
        (status, json) = await Gm("/registration/codes", new { count = 2, maxUses = 1, days = 1, note = "integration" });
        Check("批量生成注册码", status == 201 && json.GetProperty("codes").GetArrayLength() == 2);
        string code = json.GetProperty("codes")[0].GetString()!;
        string disabled = json.GetProperty("codes")[1].GetString()!;
        (status, json) = await Gm("/registration/codes/" + disabled, new { disabled = true }, "PATCH");
        (status, json) = await Gm("/register", new { username = "disabled_" + suffix, password = "register-pass-123", registrationCode = disabled });
        Check("停用注册码不能注册", status == 400);
        var races = await Task.WhenAll(
            Gm("/register", new { username = "reg_gm_" + suffix, password = "register-pass-123", registrationCode = code }),
            PostJsonAsync("/account-api/register", new { username = "reg_gw_" + suffix, password = "register-pass-123", registrationCode = code }));
        Check("GM 与网关并发兑换单次码只成功一次", races.Count(x => x.Item1 == 201) == 1 && races.Count(x => x.Item1 == 400) == 1);
        (status, json) = await Gm("/registration/codes/" + code + "/history", method: "GET");
        Check("记录兑换账号", status == 200 && json.GetProperty("history").GetArrayLength() == 1);
        await Gm("/registration/settings", new { requireCode = false }, "PUT");
        (status, json) = await Gm("/register", new { username = "open_" + suffix, password = "register-pass-123" });
        Check("开放注册可无码注册且不预建角色", status == 201 && !json.GetProperty("hasCharacter").GetBoolean());
        Console.WriteLine($"GM integration failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    public async Task<int> RunFullAsync(string statePath)
    {
        Console.WriteLine($"== 本地网关验收 {baseUrl} ==");

        // 1. 旧测试账号仍然可以登录，并且已有角色。
        (int status, JsonElement body) = await PostJsonAsync("/account-api/login",
            new { username = TestAccount, password = TestPassword });
        Check("旧测试账号登录 200", status == 200, $"{status} {body}");
        Check("旧测试账号 UID=10001", Text(body, "uid") == "10001", Text(body, "uid"));
        Check("旧测试账号已有角色", body.TryGetProperty("hasCharacter", out var oldHas) && oldHas.GetBoolean());

        // 2. 注册校验。
        string suffix = DateTime.UtcNow.ToString("MMddHHmmss");
        string accountA = $"probe_a_{suffix}";
        string accountB = $"probe_b_{suffix}";
        string passwordA = "probe-pass-0001";
        string passwordB = "probe-pass-0002";

        (status, body) = await PostJsonAsync("/account-api/register", new { username = accountA, password = passwordA });
        Check("注册新账号 201", status == 201, $"{status} {body}");
        string uidA = Text(body, "uid");
        Check("新账号 UID 独立且大于 10001", ulong.TryParse(uidA, out ulong parsedA) && parsedA > 10001, uidA);
        Check("注册返回 hasCharacter=false",
            body.TryGetProperty("hasCharacter", out var newHas) && !newHas.GetBoolean());

        (status, body) = await PostJsonAsync("/account-api/register", new { username = accountA, password = passwordA });
        Check("重复注册返回 409", status == 409, $"{status} {body}");

        (status, body) = await PostJsonAsync("/account-api/register", new { username = "ab", password = passwordA });
        Check("非法账号返回 400", status == 400, $"{status} {body}");

        (status, body) = await PostJsonAsync("/account-api/register", new { username = "probe_short", password = "123" });
        Check("短密码返回 400", status == 400, $"{status} {body}");

        (status, body) = await PostJsonAsync("/account-api/login", new { username = accountA, password = "wrong-pass-0001" });
        Check("错误密码返回 401", status == 401, $"{status} {body}");

        (status, body) = await PostJsonAsync("/account-api/login", new { username = accountA, password = passwordA });
        Check("新账号登录 200", status == 200, $"{status} {body}");
        Check("登录返回 hasCharacter=false",
            body.TryGetProperty("hasCharacter", out var loginHas) && !loginHas.GetBoolean());

        // 3. 新账号：登录 → 无存档通知 → 创建角色 → 大厅写操作。
        using var clientA = new GameClient(baseUrl, handshakeKey);
        Reply reply = await clientA.ConnectAsync();
        Check("IKE 返回 2 IKEResp", reply.Id == 2 && reply.Status == 200, $"{reply.Status}/{reply.Id}");
        reply = await clientA.LoginAsync(accountA, passwordA);
        Check("LoginReq 返回 5 LoginResp", reply.Id == 5 && reply.Status == 200, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1001, new Nil());
        Check("新账号 1001 返回 10001 player_new_notify", reply.Id == 10001, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1004, new PlayerReg { Nickname = "探针甲", Gender = true });
        Check("创建角色 1004 返回 1002 PlayerInfo", reply.Id == 1002, $"{reply.Status}/{reply.Id}");
        PlayerInfo? created = reply.Id == 1002 ? PlayerInfo.Parser.ParseFrom(reply.Body) : null;
        Check("1002.Acc.Id 等于账号 UID", created?.Acc?.Id.ToString() == uidA, created?.Acc?.Id.ToString());
        Check("1002.Acc.NickName 正确", created?.Acc?.NickName == "探针甲", created?.Acc?.NickName);
        Check("1002 携带展示角色", created?.Board.Count > 0, created?.Board.Count.ToString());
        Check("1002 携带初始资源", created?.Res.Count > 0, created?.Res.Count.ToString());

        reply = await clientA.SendAsync(1004, new PlayerReg { Nickname = "探针甲", Gender = true });
        Check("同名重复创建按重试处理（1002）", reply.Id == 1002, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1004, new PlayerReg { Nickname = "探针丙", Gender = true });
        Check("已有角色再建不同昵称返回 1005", reply.Id == 1005, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1004, new PlayerReg { Nickname = "1bad", Gender = true });
        Check("非法昵称返回 1005 Error", reply.Id == 1005, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1021, new PlayerBoardSetReq { Ids = { 410301, 410111 } });
        Check("展示保存 1021 返回 1022", reply.Id == 1022, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1015, new NewbieInfo { GroupId = 2, StepId = 7 });
        Check("引导上报 1015 返回 1016", reply.Id == 1016, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(4916, new Nil());
        Check("星塔入口好感 4916 返回 4917", reply.Id == 4917, $"{reply.Status}/{reply.Id}");
        NPCAffinityBookGetResp? affinity = reply.Id == 4917 ? NPCAffinityBookGetResp.Parser.ParseFrom(reply.Body) : null;
        Check("4917 为 0 条好感", affinity is { Infos.Count: 0, Number: 0 },
            $"{affinity?.Infos.Count}/{affinity?.Number}");

        reply = await clientA.SendAsync(1301, new Nil());
        Check("星塔成长 1301 返回 1302", reply.Id == 1302, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1012, new Ping());
        Check("Ping 1012 返回 1013", reply.Id == 1013, $"{reply.Status}/{reply.Id}");

        // 4. 第二个账号：独立 UID、独立存档，不能看到甲的数据。
        (status, body) = await PostJsonAsync("/account-api/register", new { username = accountB, password = passwordB });
        Check("注册第二个账号 201", status == 201, $"{status} {body}");
        string uidB = Text(body, "uid");
        Check("两个账号 UID 不同", uidA != uidB && uidB.Length > 0, $"{uidA} vs {uidB}");

        using var clientB = new GameClient(baseUrl, handshakeKey);
        await clientB.ConnectAsync();
        reply = await clientB.LoginAsync(accountB, passwordB);
        Check("第二账号登录成功", reply.Id == 5, $"{reply.Status}/{reply.Id}");
        reply = await clientB.SendAsync(1001, new Nil());
        Check("第二账号 1001 返回 10001", reply.Id == 10001, $"{reply.Status}/{reply.Id}");
        reply = await clientB.SendAsync(1004, new PlayerReg { Nickname = "探针乙", Gender = false });
        Check("第二账号创建角色返回 1002", reply.Id == 1002, $"{reply.Status}/{reply.Id}");
        reply = await clientB.SendAsync(1021, new PlayerBoardSetReq { Ids = { 410112 } });
        Check("第二账号保存展示返回 1022", reply.Id == 1022, $"{reply.Status}/{reply.Id}");

        reply = await clientA.SendAsync(1001, new Nil());
        PlayerInfo? reloaded = reply.Id == 1002 ? PlayerInfo.Parser.ParseFrom(reply.Body) : null;
        Check("甲账号展示未被乙覆盖",
            reloaded is not null && reloaded.Board.Count == 2 && reloaded.Board[0] == 410301,
            string.Join(",", reloaded?.Board ?? []));
        PlayerInfo? second = null;
        reply = await clientB.SendAsync(1001, new Nil());
        second = reply.Id == 1002 ? PlayerInfo.Parser.ParseFrom(reply.Body) : null;
        Check("乙账号展示独立", second is not null && second.Board.Count == 1 && second.Board[0] == 410112,
            string.Join(",", second?.Board ?? []));
        Check("乙账号昵称独立", second?.Acc?.NickName == "探针乙", second?.Acc?.NickName);

        // 5. 旧测试账号走游戏协议进场。
        using var oldClient = new GameClient(baseUrl, handshakeKey);
        await oldClient.ConnectAsync();
        reply = await oldClient.LoginAsync(TestAccount, TestPassword);
        Check("旧账号游戏登录成功", reply.Id == 5, $"{reply.Status}/{reply.Id}");
        reply = await oldClient.SendAsync(1001, new Nil());
        PlayerInfo? stored = reply.Id == 1002 ? PlayerInfo.Parser.ParseFrom(reply.Body) : null;
        Check("旧账号 1001 返回已有存档", stored?.Acc?.Id == 10001 && stored.Acc.NickName.Length > 0,
            stored?.Acc?.NickName);

        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
        {
            baseUrl,
            accountA,
            passwordA,
            uidA,
            accountB,
            passwordB,
            uidB,
            boardA = 410301,
            boardB = 410112,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"状态已保存：{statePath}");
        return Report();
    }

    /// <summary>并发校验：重复注册只成功一次、并发创建角色只落一份存档、并发写展示不互相破坏。</summary>
    public async Task<int> RunRaceAsync()
    {
        Console.WriteLine($"== 并发/重试校验 {baseUrl} ==");
        string suffix = DateTime.UtcNow.ToString("HHmmss");
        string account = $"probe_race_{suffix}";
        const string password = "probe-race-0001";
        string nickname = "并发" + suffix;

        (int status, JsonElement body) = await PostJsonAsync("/account-api/register", new { username = account, password });
        Check("并发测试账号注册 201", status == 201, $"{status} {body}");
        string uid = Text(body, "uid");

        var duplicate = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PostJsonAsync("/account-api/register", new { username = account, password })));
        Check("并发重复注册只返回 409", duplicate.All(item => item.Status == 409),
            string.Join(",", duplicate.Select(item => item.Status)));

        // 4 个独立会话并发创建同名角色：重试幂等，全部 1002，UID 一致。
        var createTasks = Enumerable.Range(0, 4).Select(async index =>
        {
            var client = new GameClient(baseUrl, handshakeKey);
            try
            {
                await client.ConnectAsync();
                Reply login = await client.LoginAsync(account, password);
                if (login.Id != 5) return (Reply: login, Player: (PlayerInfo?)null);
                Reply created = await client.SendAsync(1004, new PlayerReg { Nickname = nickname, Gender = true });
                return (Reply: created, Player: created.Id == 1002 ? PlayerInfo.Parser.ParseFrom(created.Body) : null);
            }
            finally
            {
                client.Dispose();
            }
        }).ToArray();
        var created = await Task.WhenAll(createTasks);
        Check("并发创建角色全部 1002", created.All(item => item.Reply.Id == 1002),
            string.Join(",", created.Select(item => item.Reply.Id)));
        Check("并发创建角色 UID 一致", created.All(item => item.Player?.Acc?.Id.ToString() == uid),
            string.Join(",", created.Select(item => item.Player?.Acc?.Id.ToString())));

        // 并发写展示：每个会话提交不同组合，最终必须恰好是其中一组，不能混合。
        uint[][] boards =
        [
            [410301, 410302],
            [410301, 410303],
            [410302, 410303],
            [410301, 410302, 410303],
        ];
        var writeTasks = boards.Select(async (board, index) =>
        {
            var client = new GameClient(baseUrl, handshakeKey);
            try
            {
                await client.ConnectAsync();
                if ((await client.LoginAsync(account, password)).Id != 5) return (short)0;
                return (await client.SendAsync(1021, new PlayerBoardSetReq { Ids = { board } })).Id;
            }
            finally
            {
                client.Dispose();
            }
        }).ToArray();
        short[] writeIds = await Task.WhenAll(writeTasks);
        Check("并发保存展示全部 1022", writeIds.All(id => id == 1022), string.Join(",", writeIds));

        using var reader = new GameClient(baseUrl, handshakeKey);
        await reader.ConnectAsync();
        await reader.LoginAsync(account, password);
        Reply data = await reader.SendAsync(1001, new Nil());
        PlayerInfo? final = data.Id == 1002 ? PlayerInfo.Parser.ParseFrom(data.Body) : null;
        uint[] finalBoard = final?.Board.ToArray() ?? [];
        bool matchesSubmitted = boards.Any(board => board.OrderBy(id => id).SequenceEqual(finalBoard.OrderBy(id => id)));
        Check("并发写展示未出现混合状态", matchesSubmitted, string.Join(",", finalBoard));
        Check("并发创建后仍只有一个角色",
            (await TryCreateAsync(account, password, nickname + "x")).Id == 1005, "expected 1005");
        return Report();
    }

    private async Task<Reply> TryCreateAsync(string account, string password, string nickname)
    {
        using var client = new GameClient(baseUrl, handshakeKey);
        await client.ConnectAsync();
        await client.LoginAsync(account, password);
        return await client.SendAsync(1004, new PlayerReg { Nickname = nickname, Gender = true });
    }

    public async Task<int> VerifyPersistedAsync(string statePath)
    {
        Console.WriteLine($"== 重启后持久化校验 {baseUrl} ==");
        if (!File.Exists(statePath))
        {
            Console.Error.WriteLine("缺少 probe-state.json，请先运行 full 模式");
            return 2;
        }

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
        string accountA = state.RootElement.GetProperty("accountA").GetString()!;
        string passwordA = state.RootElement.GetProperty("passwordA").GetString()!;
        string accountB = state.RootElement.GetProperty("accountB").GetString()!;
        string passwordB = state.RootElement.GetProperty("passwordB").GetString()!;
        uint boardA = state.RootElement.GetProperty("boardA").GetUInt32();
        uint boardB = state.RootElement.GetProperty("boardB").GetUInt32();
        string uidA = state.RootElement.GetProperty("uidA").GetString()!;
        string uidB = state.RootElement.GetProperty("uidB").GetString()!;

        PlayerInfo? playerA = await ReadPlayerAsync(accountA, passwordA);
        Check("重启后甲账号存档可读", playerA?.Acc?.Id.ToString() == uidA, playerA?.Acc?.Id.ToString());
        Check("重启后甲展示角色仍在", playerA?.Board.Contains(boardA) == true,
            string.Join(",", playerA?.Board ?? []));
        Check("重启后甲引导进度仍在（组 2 = 7）",
            playerA?.Acc?.Newbies.Any(item => item.GroupId == 2 && item.StepId == 7) == true,
            string.Join(",", playerA?.Acc?.Newbies.Select(item => $"{item.GroupId}:{item.StepId}") ?? []));

        PlayerInfo? playerB = await ReadPlayerAsync(accountB, passwordB);
        Check("重启后乙账号存档可读", playerB?.Acc?.Id.ToString() == uidB, playerB?.Acc?.Id.ToString());
        Check("重启后乙展示独立", playerB?.Board.Count == 1 && playerB.Board[0] == boardB,
            string.Join(",", playerB?.Board ?? []));
        return Report();
    }

    private async Task<PlayerInfo?> ReadPlayerAsync(string account, string password)
    {
        using var client = new GameClient(baseUrl, handshakeKey);
        await client.ConnectAsync();
        Reply login = await client.LoginAsync(account, password);
        if (login.Id != 5) return null;
        Reply data = await client.SendAsync(1001, new Nil());
        return data.Id == 1002 ? PlayerInfo.Parser.ParseFrom(data.Body) : null;
    }

    private async Task<(int Status, JsonElement Body)> PostJsonAsync(string path, object payload)
    {
        using var response = await http.PostAsync(baseUrl + path,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        string text = await response.Content.ReadAsStringAsync();
        try
        {
            return ((int)response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
        }
        catch (JsonException)
        {
            return ((int)response.StatusCode, JsonDocument.Parse("{}").RootElement.Clone());
        }
    }

    private static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ToString()
            : "";

    private void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : $"  ← {detail}")}");
    }

    private int Report()
    {
        Console.WriteLine(failures == 0 ? "全部通过" : $"失败 {failures} 项");
        return failures == 0 ? 0 : 1;
    }

    public void Dispose() => http.Dispose();
}

internal sealed record Reply(int Status, short Id, byte[] Body);

/// <summary>独立实现的客户端帧：AES-GCM 握手 + 置乱、ChaCha20-Poly1305 会话。</summary>
internal sealed class GameClient(string baseUrl, byte[] handshakeKey) : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private ECDiffieHellman? keyPair;
    private byte[] sessionKey = [];
    private string token = "";
    private ushort sequence;

    public async Task<Reply> ConnectAsync()
    {
        keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECPoint point = keyPair.ExportParameters(false).Q;
        byte[] publicKey = [4, .. point.X!, .. point.Y!];
        byte[] frame = BuildHandshake(1, new IKEReq
        {
            ProtoVersion = 1,
            ClientTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PubKey = ByteString.CopyFrom(publicKey),
        });
        (int status, byte[] payload) = await PostAsync(frame, null);
        if (status != 200) return new Reply(status, 0, payload);

        (short id, byte[] body) = ReadHandshake(payload);
        if (id != 2) return new Reply(status, id, body);
        IKEResp response = IKEResp.Parser.ParseFrom(body);
        token = response.Token;
        sessionKey = DeriveSessionKey(publicKey, response.PubKey.ToByteArray());
        return new Reply(status, id, body);
    }

    public Task<Reply> LoginAsync(string account, string password) =>
        SendAsync(4, new LoginReq { Account = new Regular { Name = account, Pass = password }, Platform = Platform.Android });

    public async Task<Reply> SendAsync(short id, IMessage message)
    {
        byte[] frame = BuildSession(id, message);
        (int status, byte[] payload) = await PostAsync(frame, token);
        if (status != 200) return new Reply(status, 0, payload);
        (short replyId, byte[] body) = ReadSession(payload);
        return new Reply(status, replyId, body);
    }

    private async Task<(int Status, byte[] Payload)> PostAsync(byte[] frame, string? sessionToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/agent-zone-1/");
        request.Content = new ByteArrayContent(frame);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (!string.IsNullOrEmpty(sessionToken)) request.Headers.Add("X-Token", sessionToken);
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private byte[] BuildHandshake(short id, IMessage message)
    {
        byte[] body = message.ToByteArray();
        byte[] plaintext = new byte[2 + body.Length];
        BinaryPrimitives.WriteInt16BigEndian(plaintext, id);
        body.CopyTo(plaintext, 2);
        var frame = new byte[NonceSize + plaintext.Length + TagSize];
        Span<byte> nonce = frame.AsSpan(0, NonceSize);
        Span<byte> ciphertext = frame.AsSpan(NonceSize, plaintext.Length);
        Span<byte> tag = frame.AsSpan(NonceSize + plaintext.Length);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(handshakeKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, nonce);
        }
        Scramble(frame, encrypt: true);
        return frame;
    }

    private (short Id, byte[] Body) ReadHandshake(byte[] payload)
    {
        byte[] frame = (byte[])payload.Clone();
        Scramble(frame, encrypt: false);
        ReadOnlySpan<byte> nonce = frame.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = frame.AsSpan(frame.Length - TagSize);
        ReadOnlySpan<byte> ciphertext = frame.AsSpan(NonceSize, frame.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(handshakeKey, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, nonce);
        }
        return (BinaryPrimitives.ReadInt16BigEndian(plaintext), plaintext[2..]);
    }

    private byte[] BuildSession(short id, IMessage message)
    {
        byte[] body = message.ToByteArray();
        var plaintext = new byte[12 + body.Length];
        BinaryPrimitives.WriteInt64BigEndian(plaintext, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(8), sequence++);
        BinaryPrimitives.WriteInt16BigEndian(plaintext.AsSpan(10), id);
        body.CopyTo(plaintext, 12);
        var frame = new byte[NonceSize + plaintext.Length + TagSize];
        Span<byte> nonce = frame.AsSpan(0, NonceSize);
        Span<byte> ciphertext = frame.AsSpan(NonceSize, plaintext.Length);
        Span<byte> tag = frame.AsSpan(NonceSize + plaintext.Length);
        RandomNumberGenerator.Fill(nonce);
        using (var cipher = new ChaCha20Poly1305(sessionKey))
        {
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, nonce);
        }
        return frame;
    }

    private (short Id, byte[] Body) ReadSession(byte[] payload)
    {
        ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = payload.AsSpan(payload.Length - TagSize);
        ReadOnlySpan<byte> ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];
        using (var cipher = new ChaCha20Poly1305(sessionKey))
        {
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, nonce);
        }
        return (BinaryPrimitives.ReadInt16BigEndian(plaintext), plaintext[2..]);
    }

    private byte[] DeriveSessionKey(byte[] clientPublic, byte[] serverPublic)
    {
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });
        byte[] shared = keyPair!.DeriveRawSecretAgreement(peer.PublicKey);
        var info = new byte[clientPublic.Length];
        for (int index = 0; index < info.Length; index++)
        {
            int serverByte = serverPublic[index % serverPublic.Length];
            info[index] = (byte)(clientPublic[index] ^
                                 (clientPublic[index] > serverByte ? serverByte << 1 : serverByte >> 1));
        }
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, serverPublic, info);
        CryptographicOperations.ZeroMemory(shared);
        return key;
    }

    private void Scramble(Span<byte> frame, bool encrypt)
    {
        int mask = frame.Length & 255;
        for (int index = 0; index < frame.Length; index++)
        {
            int byteKey = handshakeKey[index % handshakeKey.Length];
            if (encrypt)
            {
                int value = frame[index] ^ byteKey;
                frame[index] = (byte)(((value << 1) | (value >> 7)) ^ mask);
            }
            else
            {
                int value = frame[index] ^ mask;
                frame[index] = (byte)(((value >> 1) | (value << 7)) ^ byteKey);
            }
        }
    }

    public void Dispose()
    {
        http.Dispose();
        keyPair?.Dispose();
    }
}
