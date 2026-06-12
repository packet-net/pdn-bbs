using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Bbs.Core;

/// <summary>
/// Argon2id password hashing for the BBS mail-password (the credential an external mail
/// client — iPhone Mail over IMAP — authenticates with, alongside the callsign). Produces the
/// self-describing PHC-format encoded hash that is stored verbatim in <c>bbs.db</c>'s
/// <c>mail_auth</c> table and verified back without any external parameter state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm: Argon2id</b> (the hybrid OWASP recommends for password storage — the
/// data-dependent + data-independent mix that resists both GPU and side-channel attack).
/// Provided by the maintained, MIT-licensed <c>Konscious.Security.Cryptography.Argon2</c>.
/// (Mirrors the packet.net node's web-control-API hasher one-for-one, so the two share proven
/// parameters and format.)
/// </para>
/// <para>
/// <b>Parameters</b> (OWASP "Argon2id" minimum, the memory-leaning second configuration):
/// <c>m = 19456 KiB (19 MiB)</c>, <c>t = 2</c> iterations, <c>p = 1</c> degree of parallelism,
/// a 16-byte CSPRNG salt, and a 32-byte digest. These are encoded into the stored string, so a
/// later parameter bump verifies old hashes at their original cost and only new hashes use the
/// new cost (graceful migration).
/// </para>
/// <para>
/// <b>Per-user salt</b> from <see cref="RandomNumberGenerator"/> (a CSPRNG), so two users with
/// the same password get different hashes and precomputation / rainbow tables are useless.
/// </para>
/// <para>
/// <b>Fixed-time verify.</b> The digest comparison uses
/// <see cref="CryptographicOperations.FixedTimeEquals"/> so a verify takes the same time whether
/// the first byte or the last byte differs — no timing oracle on the hash. (The Argon2 derivation
/// itself dominates the wall-clock and is input-independent in length.)
/// </para>
/// <para>
/// <b>Encoded format</b> (PHC string format, the de-facto standard so the hash is self-describing
/// and portable):
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>
/// (standard base64, no padding — the PHC convention).
/// </para>
/// </remarks>
public static class PasswordHasher
{
    // OWASP-recommended Argon2id parameters (memory-leaning configuration).
    private const int MemoryKib = 19456;     // 19 MiB
    private const int Iterations = 2;        // time cost (t)
    private const int Parallelism = 1;       // degree of parallelism (p)
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Argon2Version = 19;    // 0x13 — the Argon2 v1.3 constant in the PHC string

    /// <summary>
    /// Hash <paramref name="password"/> with a fresh per-call CSPRNG salt and the OWASP Argon2id
    /// parameters, returning the full PHC-encoded string to store.
    /// </summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] digest = Derive(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return Encode(MemoryKib, Iterations, Parallelism, salt, digest);
    }

    /// <summary>
    /// Verify <paramref name="password"/> against a stored PHC-encoded <paramref name="encodedHash"/>.
    /// Re-derives at the hash's <em>own</em> recorded parameters (so old hashes still verify after a
    /// parameter bump) and compares in fixed time. Returns <c>false</c> — never throws — on any
    /// malformed / unparseable stored hash.
    /// </summary>
    public static bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(encodedHash) || !TryDecode(encodedHash, out DecodedHash p))
        {
            return false;
        }

        byte[] candidate = Derive(password, p.Salt, p.MemoryKib, p.Iterations, p.Parallelism, p.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, p.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int outputBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(outputBytes);
    }

    private static string Encode(int memoryKib, int iterations, int parallelism, byte[] salt, byte[] hash)
    {
        string saltB64 = Base64NoPad(salt);
        string hashB64 = Base64NoPad(hash);
        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v={Argon2Version}$m={memoryKib},t={iterations},p={parallelism}${saltB64}${hashB64}");
    }

    // PHC params decoded from a stored hash, re-derived against on verify.
    private readonly record struct DecodedHash(int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryDecode(string encoded, out DecodedHash decoded)
    {
        decoded = default;
        // $argon2id$v=19$m=..,t=..,p=..$<salt>$<hash>  → ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        string[] parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id")
        {
            return false;
        }

        if (!parts[2].StartsWith("v=", StringComparison.Ordinal))
        {
            return false;
        }

        int memoryKib = 0, iterations = 0, parallelism = 0;
        foreach (string kv in parts[3].Split(','))
        {
            string[] eq = kv.Split('=', 2);
            if (eq.Length != 2 || !int.TryParse(eq[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }

            switch (eq[0])
            {
                case "m": memoryKib = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memoryKib <= 0 || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        if (!TryBase64NoPad(parts[4], out byte[] salt) || !TryBase64NoPad(parts[5], out byte[] hash)
            || salt.Length == 0 || hash.Length == 0)
        {
            return false;
        }

        decoded = new DecodedHash(memoryKib, iterations, parallelism, salt, hash);
        return true;
    }

    private static string Base64NoPad(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryBase64NoPad(string s, out byte[] bytes)
    {
        // Restore the padding the PHC format strips before decoding.
        int pad = (4 - (s.Length % 4)) % 4;
        string padded = pad == 0 ? s : s + new string('=', pad);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
