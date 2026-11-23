using System.Data;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Proto;

internal sealed partial class LocalPlayerStore
{
    public GameCatalog Catalog { get; private set; } = null!;

    public long PasswordRevision(ulong uid) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_revision FROM account_security WHERE uid=$uid";
        command.Parameters.AddWithValue("$uid", FormatUid(uid));
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    });

    private static void RedeemRegistrationCode(SqliteConnection connection, SqliteTransaction transaction,
        ulong uid, string? rawCode, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT require_code FROM registration_settings WHERE id=1";
        bool required = Convert.ToInt64(command.ExecuteScalar()) == 1;
        string code = (rawCode ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0 && !required) return;
        command.CommandText = """
            UPDATE registration_codes SET uses=uses+1
            WHERE code=$code AND disabled=0 AND uses<max_uses AND (expires_at=0 OR expires_at>$now)
            """;
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$now", now);
        if (command.ExecuteNonQuery() != 1) throw new GameRuleException("注册码无效、已过期、已停用或次数已用完");
        command.CommandText = "INSERT INTO registration_redemptions(code,uid,used_at) VALUES($code,$uid,$now)";
        command.Parameters.AddWithValue("$uid", FormatUid(uid));
        command.ExecuteNonQuery();
    }

    public Mails ReadMails(ulong uid) => WithConnection(connection =>
    {
        var result = new Mails();
        result.List.AddRange(LoadMails(connection, null, uid).Where(x => x.Deadline == 0 || x.Deadline > DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return result;
    });

    private static List<Mail> LoadMails(SqliteConnection connection, SqliteTransaction? transaction, ulong uid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT mail FROM game_mail WHERE uid=$uid ORDER BY id DESC";
        command.Parameters.AddWithValue("$uid", FormatUid(uid));
        using var reader = command.ExecuteReader();
        var result = new List<Mail>();
        while (reader.Read()) result.Add(Mail.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0)));
        return result;
    }

    // Mail state and all attachment rewards commit together under the same writer lock.
    public IMessage MutateMail(ulong uid, short requestId, uint id, ulong flag, bool pin = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                var player = ReadPlayer(connection, transaction, uid) ?? throw new GameRuleException("请先创建角色");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var all = LoadMails(connection, transaction, uid);
                if (id == 0 && requestId is not (9007 or 9010)) throw new GameRuleException("邮件 ID 无效");
                var selected = all.Where(x => id == 0 || (x.Id == id && x.Flag == flag)).ToList();
                if (id != 0 && selected.Count == 0) throw new GameRuleException("邮件不存在或标记不匹配");
                var received = new MailRecvResp { Items = new ChangeInfo() };
                var removed = new MailRemoveResp();
                foreach (var mail in selected)
                {
                    bool expired = mail.Deadline != 0 && mail.Deadline <= now;
                    if (expired && requestId != 9010)
                    {
                        if (id != 0) throw new GameRuleException("邮件已过期");
                        continue;
                    }
                    if (requestId == 9004) mail.Read = true;
                    if (requestId == 9013) mail.Pin = pin;
                    if (requestId == 9007 && !mail.Recv && mail.Attachments.Count > 0)
                    {
                        received.Items.Props.AddRange(Catalog.Grant(player, mail.Attachments).Props);
                        mail.Read = true;
                        mail.Recv = true;
                        received.Ids.Add(mail.Id);
                    }
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("$id", mail.Id);
                    command.Parameters.AddWithValue("$uid", FormatUid(uid));
                    if (requestId == 9010)
                    {
                        bool canRemove = !mail.Pin && (expired || (mail.Read && (mail.Recv || mail.Attachments.Count == 0)));
                        if (!canRemove)
                        {
                            if (id != 0) throw new GameRuleException("不能删除置顶、未读或未领取附件的邮件");
                            continue;
                        }
                        command.CommandText = "DELETE FROM game_mail WHERE id=$id AND uid=$uid";
                        removed.Ids.Add(mail.Id);
                    }
                    else
                    {
                        command.CommandText = "UPDATE game_mail SET mail=$mail WHERE id=$id AND uid=$uid";
                        command.Parameters.AddWithValue("$mail", mail.ToByteArray());
                    }
                    command.ExecuteNonQuery();
                }
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE players SET player_info=$data, updated_at=$at WHERE uid=$uid";
                player.ServerTs = 0;
                update.Parameters.AddWithValue("$data", player.ToByteArray());
                update.Parameters.AddWithValue("$uid", FormatUid(uid));
                update.Parameters.AddWithValue("$at", now);
                update.ExecuteNonQuery();
                transaction.Commit();
                return requestId switch
                {
                    9004 => new UI32 { Value = id },
                    9007 => received,
                    9010 => removed,
                    9013 => new MailPinRequest { Id = id, Flag = flag, Pin = pin },
                    _ => throw new InvalidOperationException("Unknown mail mutation"),
                };
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < BusyRetries)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
