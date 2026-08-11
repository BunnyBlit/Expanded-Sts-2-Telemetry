using System;
using System.Security.Cryptography;
using System.Text;

namespace ExpandedTelemetry;

// Deterministic pseudonymous identifiers for telemetry. Raw Steam64 ids and run
// start-times are hashed into RFC 4122 v5 (SHA-1, name-based) UUIDs so they never
// leave the machine in the clear, while staying stable and joinable across every
// event of a run. Player and run use separate namespaces so their id spaces can
// never collide. Verified to match the reference uuid5 (Python/Postgres/Rust).
internal static class TelemetryId
{
    // Fixed namespace UUIDs. DO NOT CHANGE — either change repartitions all
    // previously-emitted ids and breaks joins against already-ingested data.
    private static readonly Guid PlayerNamespace = new("d7a4c9e2-3f81-4b6a-9c2e-5a1b8f0e4d63");
    private static readonly Guid RunNamespace = new("e2f5b1a7-6c94-4d38-8a1f-2b7e9c0d5a46");

    // Steam64 NetId -> stable pseudonymous player_id.
    public static string Player(ulong steamId) => Uuid5(PlayerNamespace, steamId.ToString());

    // Run start-time (Unix seconds) -> stable run_id for the whole run.
    public static string Run(long startTime) => Uuid5(RunNamespace, startTime.ToString());

    // RFC 4122 §4.3 name-based UUID (version 5, SHA-1). Uses the .NET 8+ big-endian
    // Guid byte APIs so the layout matches the RFC (and every other uuid5 impl).
    public static string Uuid5(Guid ns, string name)
    {
        byte[] nsBytes = ns.ToByteArray(bigEndian: true);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] data = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, data, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, nsBytes.Length, nameBytes.Length);

        byte[] hash = SHA1.HashData(data);

        Span<byte> uuid = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(uuid, bigEndian: true).ToString();
    }
}
