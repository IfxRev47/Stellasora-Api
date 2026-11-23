using System.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Proto;

internal sealed partial class LocalPlayerStore
{
    private const int FriendLimit = 50, InviteLimit = 30, FriendDailyEnergy = 30;
    // Client refreshes at 05:00 UTC+8. Gift quantity is 1, matching Config.json.
    internal static long FriendDay(long seconds) => (seconds + 3 * 3600) / 86400;

    private static SqliteCommand FriendSql(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return command;
    }

    private static List<ulong> FriendIds(SqliteConnection db, SqliteTransaction tx, string sql, params (string Key, object Value)[] args)
    {
        using var command = FriendSql(db, tx, sql, args); using var reader = command.ExecuteReader();
        var ids = new List<ulong>(); while (reader.Read()) ids.Add(ulong.Parse(reader.GetString(0))); return ids;
    }

    private static long FriendCount(SqliteConnection db, SqliteTransaction tx, ulong uid) {
        using var command = FriendSql(db, tx, "SELECT COUNT(*) FROM friend_links WHERE owner=$uid", ("$uid",FormatUid(uid)));
        return (long)command.ExecuteScalar()!;
    }
    private static bool AreFriends(SqliteConnection db, SqliteTransaction tx, ulong uid, ulong other) {
        using var command = FriendSql(db, tx, "SELECT 1 FROM friend_links WHERE owner=$uid AND target=$other", ("$uid",FormatUid(uid)),("$other",FormatUid(other)));
        return command.ExecuteScalar() is not null;
    }
    private T FriendTransaction<T>(ulong uid, Func<SqliteConnection,SqliteTransaction,PlayerInfo,T> work)
    {
        for (int attempt=0;;attempt++) {
            try {
                using var db=OpenConnection(); using var tx=db.BeginTransaction(IsolationLevel.Serializable,deferred:false);
                var player=ReadPlayer(db,tx,uid) ?? throw new GameRuleException("请先创建角色");
                T result=work(db,tx,player); tx.Commit(); return result;
            } catch(SqliteException error) when(IsBusy(error) && attempt<BusyRetries) { Thread.Sleep(25*(attempt+1)); }
        }
    }

    public void TouchLogin(ulong uid) => WithConnection(db => {
        using var command=FriendSql(db,null,"INSERT INTO player_presence(uid,last_login_ns) VALUES($uid,$at) ON CONFLICT(uid) DO UPDATE SET last_login_ns=$at",
            ("$uid",FormatUid(uid)),("$at",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1_000_000));
        return command.ExecuteNonQuery();
    });

    private Friend? FriendProfile(SqliteConnection db,SqliteTransaction tx,ulong uid)
    {
        var player=ReadPlayer(db,tx,uid); if(player is null) return null;
        var acc=player.Acc;
        using var presence=FriendSql(db,tx,"SELECT last_login_ns FROM player_presence WHERE uid=$uid",("$uid",FormatUid(uid)));
        var profile=new Friend { Id=uid,NickName=acc.NickName,Hashtag=acc.Hashtag,HeadIcon=acc.HeadIcon,
            LastLoginTime=Convert.ToInt64(presence.ExecuteScalar() ?? acc.CreateTime*1_000_000_000),
            WorldClass=player.WorldClass?.Cur ?? 1,TitlePrefix=acc.TitlePrefix,TitleSuffix=acc.TitleSuffix,Signature=acc.Signature };
        profile.CharShows.AddRange(acc.Chars.Select(x=>x.Clone())); profile.Honors.AddRange(player.Honors.Select(x=>x.Clone()));
        profile.DormOutInfo=new DormOutInfo(); return profile;
    }

    private static uint ReceivedFriendEnergy(SqliteConnection db,SqliteTransaction tx,ulong uid,long day) {
        using var command=FriendSql(db,tx,"SELECT COUNT(*) FROM friend_energy WHERE target=$uid AND day=$day AND claimed=1",("$uid",FormatUid(uid)),("$day",day));
        return checked((uint)(long)command.ExecuteScalar()!);
    }
    private FriendListGetResp FriendList(SqliteConnection db,SqliteTransaction tx,ulong uid)
    {
        long day=FriendDay(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result=new FriendListGetResp { ReceiveEnergyCnt=ReceivedFriendEnergy(db,tx,uid,day) };
        foreach(ulong other in FriendIds(db,tx,"SELECT target FROM friend_links WHERE owner=$uid ORDER BY target",("$uid",FormatUid(uid)))) {
            var profile=FriendProfile(db,tx,other); if(profile is null) continue;
            using var star=FriendSql(db,tx,"SELECT star FROM friend_links WHERE owner=$uid AND target=$other",("$uid",FormatUid(uid)),("$other",FormatUid(other)));
            using var sent=FriendSql(db,tx,"SELECT 1 FROM friend_energy WHERE sender=$uid AND target=$other AND day=$day",("$uid",FormatUid(uid)),("$other",FormatUid(other)),("$day",day));
            using var received=FriendSql(db,tx,"SELECT claimed FROM friend_energy WHERE target=$uid AND sender=$other AND day=$day",("$uid",FormatUid(uid)),("$other",FormatUid(other)),("$day",day));
            object? claimed=received.ExecuteScalar();
            result.Friends.Add(new FriendDetail { Base=profile,Star=Convert.ToInt64(star.ExecuteScalar())==1,SendEnergy=sent.ExecuteScalar() is not null,
                GetEnergy=claimed is null ? 0u : Convert.ToInt64(claimed)==1 ? 2u : 1u });
        }
        foreach(ulong sender in FriendIds(db,tx,"SELECT sender FROM friend_invites WHERE target=$uid ORDER BY created_at,sender",("$uid",FormatUid(uid)))) {
            var profile=FriendProfile(db,tx,sender); if(profile is not null) result.Invites.Add(profile);
        }
        return result;
    }
    public FriendListGetResp GetFriends(ulong uid) => FriendTransaction(uid,(db,tx,_)=>FriendList(db,tx,uid));

    public List<Friend> SearchFriends(ulong uid,ulong? target=null,string? name=null,bool recommend=false) => FriendTransaction(uid,(db,tx,_)=> {
        if(target.HasValue) {
            var friend=target==uid ? null : FriendProfile(db,tx,target.Value);
            return friend is null ? new List<Friend>() : new List<Friend>{friend};
        }
        string query=(name ?? "").Trim();
        if(!recommend && (query.Length==0 || query.Length>32)) throw new GameRuleException("搜索昵称需 1-32 字");
        // Decode under a single consistent snapshot; no fabricated or seed-only players.
        var ids=FriendIds(db,tx,"SELECT uid FROM players WHERE uid<>$uid ORDER BY CAST(uid AS INTEGER)",("$uid",FormatUid(uid)));
        var result=new List<Friend>();
        foreach(ulong other in ids) {
            if(recommend) {
                if(AreFriends(db,tx,uid,other) || FriendCount(db,tx,other)>=FriendLimit) continue;
                using var pending=FriendSql(db,tx,"SELECT 1 FROM friend_invites WHERE (sender=$uid AND target=$other) OR (sender=$other AND target=$uid)",("$uid",FormatUid(uid)),("$other",FormatUid(other)));
                if(pending.ExecuteScalar() is not null) continue;
            }
            var profile=FriendProfile(db,tx,other);
            if(profile is not null && (recommend || profile.NickName.Contains(query,StringComparison.OrdinalIgnoreCase))) result.Add(profile);
            if(result.Count>=20) break;
        }
        return result;
    });

    public Nil AddFriend(ulong uid,ulong other) => FriendTransaction(uid,(db,tx,_)=> {
        if(uid==other || other==0 || FriendProfile(db,tx,other) is null) throw new GameRuleException("目标角色不存在或不能添加自己");
        if(AreFriends(db,tx,uid,other)) return new Nil();
        if(FriendCount(db,tx,uid)>=FriendLimit || FriendCount(db,tx,other)>=FriendLimit) throw new GameRuleException("好友数量已达 50 人上限");
        using var existing=FriendSql(db,tx,"SELECT 1 FROM friend_invites WHERE sender=$uid AND target=$other",("$uid",FormatUid(uid)),("$other",FormatUid(other)));
        if(existing.ExecuteScalar() is not null) return new Nil();
        using var count=FriendSql(db,tx,"SELECT COUNT(*) FROM friend_invites WHERE target=$other",("$other",FormatUid(other)));
        if((long)count.ExecuteScalar()!>=InviteLimit) throw new GameRuleException("对方申请列表已满");
        using var insert=FriendSql(db,tx,"INSERT INTO friend_invites(sender,target,created_at) VALUES($uid,$other,$at)",("$uid",FormatUid(uid)),("$other",FormatUid(other)),("$at",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        insert.ExecuteNonQuery(); return new Nil();
    });

    private void AcceptFriend(SqliteConnection db,SqliteTransaction tx,ulong uid,ulong other)
    {
        if(AreFriends(db,tx,uid,other)) return;
        using var pending=FriendSql(db,tx,"SELECT 1 FROM friend_invites WHERE sender=$other AND target=$uid",("$uid",FormatUid(uid)),("$other",FormatUid(other)));
        if(pending.ExecuteScalar() is null || FriendProfile(db,tx,other) is null) throw new GameRuleException("好友申请不存在");
        if(FriendCount(db,tx,uid)>=FriendLimit || FriendCount(db,tx,other)>=FriendLimit) throw new GameRuleException("好友数量已达 50 人上限");
        using var change=FriendSql(db,tx,"""
            INSERT OR IGNORE INTO friend_links(owner,target) VALUES($uid,$other),($other,$uid);
            DELETE FROM friend_invites WHERE (sender=$uid AND target=$other) OR (sender=$other AND target=$uid);
            """,("$uid",FormatUid(uid)),("$other",FormatUid(other)));
        change.ExecuteNonQuery();
    }
    public FriendAddAgreeResp AgreeFriend(ulong uid,ulong other) => FriendTransaction(uid,(db,tx,_)=> {
        AcceptFriend(db,tx,uid,other); return new FriendAddAgreeResp { Friend=FriendProfile(db,tx,other) };
    });
    public FriendAllAgreeResp AgreeAllFriends(ulong uid) => FriendTransaction(uid,(db,tx,_)=> {
        bool limit=false;
        foreach(ulong other in FriendIds(db,tx,"SELECT sender FROM friend_invites WHERE target=$uid ORDER BY created_at,sender",("$uid",FormatUid(uid)))) {
            if(FriendCount(db,tx,uid)>=FriendLimit || FriendCount(db,tx,other)>=FriendLimit) { limit=true; continue; }
            AcceptFriend(db,tx,uid,other);
        }
        var list=FriendList(db,tx,uid); var result=new FriendAllAgreeResp { Limit=limit };
        result.Friends.AddRange(list.Friends.Select(x=>x.Base)); result.Invites.AddRange(list.Invites); return result;
    });

    public Nil DeleteFriend(ulong uid,ulong other) => FriendTransaction(uid,(db,tx,_)=> {
        using var command=FriendSql(db,tx,"""
            DELETE FROM friend_links WHERE (owner=$uid AND target=$other) OR (owner=$other AND target=$uid);
            DELETE FROM friend_invites WHERE (sender=$uid AND target=$other) OR (sender=$other AND target=$uid);
            """,("$uid",FormatUid(uid)),("$other",FormatUid(other)));
        command.ExecuteNonQuery(); return new Nil();
    });
    public Nil DeleteFriendInvites(ulong uid,IEnumerable<ulong> ids) => FriendTransaction(uid,(db,tx,_)=> {
        foreach(ulong other in CheckedFriendIds(ids)) {
            using var command=FriendSql(db,tx,"DELETE FROM friend_invites WHERE sender=$other AND target=$uid",("$uid",FormatUid(uid)),("$other",FormatUid(other)));
            command.ExecuteNonQuery();
        } return new Nil();
    });
    private static ulong[] CheckedFriendIds(IEnumerable<ulong> ids) {
        var list=ids.ToArray(); if(list.Length>50 || list.Any(x=>x==0)) throw new GameRuleException("好友列表参数无效"); return list.Distinct().ToArray();
    }
    public Nil StarFriends(ulong uid,IEnumerable<ulong> ids,bool star) => FriendTransaction(uid,(db,tx,_)=> {
        foreach(ulong other in CheckedFriendIds(ids)) {
            if(!AreFriends(db,tx,uid,other)) throw new GameRuleException("该角色不是好友");
            using var command=FriendSql(db,tx,"UPDATE friend_links SET star=$star WHERE owner=$uid AND target=$other",("$star",star?1:0),("$uid",FormatUid(uid)),("$other",FormatUid(other)));
            command.ExecuteNonQuery();
        } return new Nil();
    });
    public FriendSendEnergyResp SendFriendEnergy(ulong uid,IEnumerable<ulong> ids) => FriendTransaction(uid,(db,tx,_)=> {
        var result=new FriendSendEnergyResp(); long day=FriendDay(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach(ulong other in CheckedFriendIds(ids)) {
            if(!AreFriends(db,tx,uid,other)) throw new GameRuleException("该角色不是好友");
            using var command=FriendSql(db,tx,"INSERT OR IGNORE INTO friend_energy(sender,target,day) VALUES($uid,$other,$day)",("$uid",FormatUid(uid)),("$other",FormatUid(other)),("$day",day));
            command.ExecuteNonQuery(); result.UIds.Add(other);
        } return result;
    });
    public FriendReceiveEnergyResp ReceiveFriendEnergy(ulong uid,IEnumerable<ulong> ids) => FriendTransaction(uid,(db,tx,player)=> {
        long day=FriendDay(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result=new FriendReceiveEnergyResp { Change=new ChangeInfo(),ReceiveEnergyCnt=ReceivedFriendEnergy(db,tx,uid,day) };
        foreach(ulong other in CheckedFriendIds(ids)) {
            if(!AreFriends(db,tx,uid,other)) throw new GameRuleException("该角色不是好友");
            if(result.ReceiveEnergyCnt>=FriendDailyEnergy) break;
            using var command=FriendSql(db,tx,"UPDATE friend_energy SET claimed=1 WHERE sender=$other AND target=$uid AND day=$day AND claimed=0",("$uid",FormatUid(uid)),("$other",FormatUid(other)),("$day",day));
            if(command.ExecuteNonQuery()==1) { result.UIds.Add(other); result.ReceiveEnergyCnt++; }
        }
        if(result.UIds.Count>0) {
            player.Energy ??= new EnergyInfo(); player.Energy.Energy ??= new Energy();
            player.Energy.Energy.Primary=checked(player.Energy.Energy.Primary+(uint)result.UIds.Count);
            result.Change.Props.Add(Any.Pack(player.Energy.Energy.Clone()));
            using var save=FriendSql(db,tx,"UPDATE players SET player_info=$data,updated_at=$now WHERE uid=$uid",("$uid",FormatUid(uid)),("$data",player.ToByteArray()),("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            save.ExecuteNonQuery();
        } return result;
    });
}
