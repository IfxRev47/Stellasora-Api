using System.Security.Cryptography;
using System.Text;

internal sealed partial class LocalPlayerStore
{
    private static string TicketHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public void SaveSdkTicket(ulong uid, string token, long expiresAt) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sdk_tickets(token_hash,uid,expires_at,password_revision)
            VALUES($hash,$uid,$expires,COALESCE((SELECT password_revision FROM account_security WHERE uid=$uid),0));
            DELETE FROM sdk_tickets WHERE expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$hash", TicketHash(token));
        command.Parameters.AddWithValue("$uid", FormatUid(uid));
        command.Parameters.AddWithValue("$expires", expiresAt);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteNonQuery();
    });

    public ulong ResolveSdkTicket(string token) => WithConnection(connection =>
    {
        if (token.Length > 256) return 0UL;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.uid FROM sdk_tickets t WHERE token_hash=$hash AND expires_at>$now
            AND password_revision=COALESCE((SELECT password_revision FROM account_security WHERE uid=t.uid),0)
            """;
        command.Parameters.AddWithValue("$hash", TicketHash(token));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteScalar() is string uid ? ulong.Parse(uid) : 0UL;
    });
}
