using System.Text;

sealed record ServerAgent(string Name, string Addr, int Status, long Zone);

sealed record ServerRule(int Platform, string? Channel, string? Version, int Op, int Action, string? Url, bool Enable);

sealed record ServerList(
    long Version,
    ServerAgent[] Agent,
    int Status = 0,
    string? Message = null,
    string? ReportEndpoint = null,
    ServerRule[]? Rules = null)
{
    public byte[] Encrypt(byte[] key)
    {
        // Field numbers match ServerListMeta and ServerAgent in meta.proto.
        using var metadata = new MemoryStream();
        WriteInteger(metadata, 1, Version);
        foreach (var entry in Agent)
        {
            using var agent = new MemoryStream();
            WriteString(agent, 1, entry.Name);
            WriteString(agent, 2, entry.Addr);
            WriteInteger(agent, 3, entry.Status);
            WriteInteger(agent, 4, entry.Zone);
            WriteBytes(metadata, 2, agent.ToArray());
        }
        WriteInteger(metadata, 3, Status);
        WriteString(metadata, 4, Message);
        WriteString(metadata, 6, ReportEndpoint);
        foreach (var entry in Rules ?? [])
        {
            using var rule = new MemoryStream();
            WriteInteger(rule, 1, entry.Platform);
            WriteString(rule, 2, entry.Channel);
            WriteString(rule, 3, entry.Version);
            WriteInteger(rule, 4, entry.Op);
            WriteInteger(rule, 5, entry.Action);
            WriteString(rule, 6, entry.Url);
            WriteInteger(rule, 7, entry.Enable ? 1 : 0);
            WriteBytes(metadata, 7, rule.ToArray());
        }

        return MetadataEnvelope.Encrypt(metadata.ToArray(), key);
    }

    private static void WriteInteger(Stream stream, uint field, long value)
    {
        if (value == 0) return;
        WriteVarint(stream, field << 3);
        WriteVarint(stream, unchecked((ulong)value));
    }

    private static void WriteString(Stream stream, uint field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            WriteBytes(stream, field, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void WriteBytes(Stream stream, uint field, byte[] value)
    {
        WriteVarint(stream, (field << 3) | 2);
        WriteVarint(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 128)
        {
            stream.WriteByte((byte)((value & 127) | 128));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}
