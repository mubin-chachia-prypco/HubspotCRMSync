using System.Security.Cryptography;

namespace Domain.Common
{
    /// <summary>
    /// Generates UUIDv7 (RFC 9562): a 48-bit Unix-millisecond timestamp prefix + random bits, so
    /// values are time-ordered / sortable AND non-enumerable. .NET 9 ships <c>Guid.CreateVersion7()</c>;
    /// this project targets net8.0 (to mirror InstaMortgageService), so we hand-roll it. Built
    /// big-endian so the Guid's string form — and the PostgreSQL <c>uuid</c> it maps to — sorts
    /// chronologically. See docs/forwarder-adapter-spec.md §15.
    /// </summary>
    public static class UuidV7
    {
        public static Guid New()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);

            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bytes[0] = (byte)(unixMs >> 40);
            bytes[1] = (byte)(unixMs >> 32);
            bytes[2] = (byte)(unixMs >> 24);
            bytes[3] = (byte)(unixMs >> 16);
            bytes[4] = (byte)(unixMs >> 8);
            bytes[5] = (byte)unixMs;

            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10xx

            return new Guid(bytes, bigEndian: true);
        }
    }
}
