using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace SuperBackend.IntegrationTests;

internal static class Totp
{
    public static string Now(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new byte[base32Secret.Length * 5 / 8];
        var bits = 0;
        var buffer = 0;
        var index = 0;
        foreach (var character in base32Secret)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits < 8)
                continue;

            bits -= 8;
            key[index++] = (byte)(buffer >> bits);
        }

        Span<byte> timeStep = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timeStep, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, timeStep);
        var number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(hash[^1] & 0x0f)) & int.MaxValue;
        return (number % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
