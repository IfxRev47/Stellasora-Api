using System.Security.Cryptography;

internal static class MetadataEnvelope
{
    public static byte[] Encrypt(byte[] payload, byte[] key)
    {
        using var cipher = Aes.Create();
        cipher.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        var ciphertext = cipher.EncryptCbc(payload, iv, PaddingMode.PKCS7);
        return [.. iv, .. ciphertext];
    }
}
