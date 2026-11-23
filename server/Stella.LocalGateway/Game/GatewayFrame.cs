using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;

internal static class GatewayFrame
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SessionRequestHeaderSize = 12;

    public static (short Id, byte[] Body) ReadRequest(byte[] wire, byte[] key, bool handshake)
    {
        var headerSize = handshake ? 2 : SessionRequestHeaderSize;
        if (wire.Length < NonceSize + TagSize + headerSize)
        {
            throw new InvalidDataException("Incomplete gateway request.");
        }

        var frame = handshake ? (byte[])wire.Clone() : wire;
        if (handshake) TransformHandshake(frame, key, encrypt: false);
        ReadOnlySpan<byte> nonce = frame.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = frame.AsSpan(frame.Length - TagSize);
        ReadOnlySpan<byte> ciphertext = frame.AsSpan(NonceSize, frame.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];
        if (handshake)
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, nonce);
        }
        else
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, nonce);
        }

        // The session request prepends the server timestamp and sequence to the message ID.
        return (BinaryPrimitives.ReadInt16BigEndian(plaintext.AsSpan(headerSize - 2)), plaintext[headerSize..]);
    }

    public static byte[] WriteResponse(short id, IMessage message, byte[] key, bool handshake)
    {
        byte[] body = message.ToByteArray();
        var plaintext = new byte[2 + body.Length];
        BinaryPrimitives.WriteInt16BigEndian(plaintext, id);
        body.CopyTo(plaintext, 2);
        byte[] frame = new byte[NonceSize + plaintext.Length + TagSize];
        Span<byte> nonce = frame.AsSpan(0, NonceSize);
        Span<byte> ciphertext = frame.AsSpan(NonceSize, plaintext.Length);
        Span<byte> tag = frame.AsSpan(NonceSize + plaintext.Length);
        RandomNumberGenerator.Fill(nonce);
        if (handshake)
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, nonce);
            TransformHandshake(frame, key, encrypt: true);
        }
        else
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, nonce);
        }

        return frame;
    }

    private static void TransformHandshake(Span<byte> frame, ReadOnlySpan<byte> key, bool encrypt)
    {
        int mask = frame.Length & 255;
        for (int index = 0; index < frame.Length; index++)
        {
            int byteKey = key[index % key.Length];
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
}
