using System.Net;
using System.Text;

internal sealed partial class LocalPlayerStore
{
    public List<LocalNotice> ReadActiveNotices() => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,category,title,content,end_at,sort,updated_at FROM game_notices
            WHERE enabled=1 AND start_at<=$now AND (end_at=0 OR end_at>$now)
            ORDER BY sort DESC,id DESC
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var result = new List<LocalNotice>();
        while (reader.Read()) result.Add(new LocalNotice((ulong)reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        return result;
    });
}

internal sealed record LocalNotice(ulong Id, string Category, string Title, string Content, long EndAt, long Sort, long UpdatedAt)
{
    public byte[] Html() => Encoding.UTF8.GetBytes(
        "<article style=\"padding:20px;line-height:1.8;overflow-wrap:anywhere\"><h2>" + WebUtility.HtmlEncode(Title) +
        "</h2><div style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(Content) + "</div></article>");
}
