using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bbs.Core;
using Packet.Fbb;

namespace Bbs.Host.Forwarding;

/// <summary>
/// The durable, crash-safe scratch area for partial INBOUND forwarding transfers (issue #38 /
/// compat spec §3.8 receiver-side restart granting). Separate from the committed message store:
/// a partial here is bytes-in-flight, not a stored message, and is promoted nowhere — it is either
/// resumed-and-committed (then discarded) or garbage-collected. One file per
/// <c>(peer base callsign, message id)</c> under <c>&lt;stateDir&gt;/inbound-partials/</c>.
///
/// <para><b>On-disk format</b> (binary, little-endian): a magic+sizes header followed by the
/// message id (UTF-8) and the raw compressed prefix received so far. Specifically:
/// <c>"PDNPART1"</c> (8 bytes) · <c>savedAtUnixSeconds</c> (int64, from the injected clock) ·
/// <c>expectedCompressedSize</c> (int32) · <c>midByteLength</c> (int32) · <c>mid</c> ·
/// <c>compressed bytes</c>. The id and save-time are embedded so a recovered file is
/// self-describing — GC ages off the stored save-time (TimeProvider-driven, not filesystem mtime),
/// and divergence checks need no sidecar.</para>
///
/// <para><b>Commit boundary.</b> Every <see cref="Save"/> writes a sibling <c>.tmp</c>, flushes it
/// to disk (<see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c>), then atomically
/// renames it over the live file. A crash therefore leaves either the previous committed partial or
/// the new one in full — never a torn file. That is the durability the issue requires: a partial
/// survives a daemon restart, with the rename as the all-or-nothing boundary.</para>
///
/// <para><b>GC.</b> <see cref="CollectStale"/> deletes partials whose file is older than the TTL
/// (default 7 days, mtime-based) so an abandoned transfer cannot grow the scratch area without
/// bound; it runs at startup and can be called periodically. A successful commit or a divergence
/// removes a partial immediately via <see cref="Discard"/>.</para>
///
/// <para>Thread-safe across sessions via a per-store lock (transfers are serialized per child anyway;
/// the lock guards concurrent peers sharing the store directory).</para>
/// </summary>
public sealed class FileInboundPartialStore
{
    private static readonly byte[] Magic = "PDNPART1"u8.ToArray();

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();

    /// <summary>Creates the store rooted at <c>&lt;stateDir&gt;/inbound-partials</c>.</summary>
    /// <param name="stateDir">The app state directory (<c>PDN_APP_STATE</c>).</param>
    /// <param name="time">Clock for GC age decisions (TimeProvider-driven, deterministic in tests).</param>
    /// <param name="ttl">How long an untouched partial lives before GC reclaims it (default 7 days).</param>
    public FileInboundPartialStore(string stateDir, TimeProvider time, TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(time);
        _root = Path.Combine(stateDir, "inbound-partials");
        _time = time;
        _ttl = ttl ?? TimeSpan.FromDays(7);
        Directory.CreateDirectory(_root);
    }

    /// <summary>A view of this store bound to one peer — the sans-IO session API (issue #38).</summary>
    public IInboundResumeStore ForPeer(string peerBaseCallsign) => new PeerView(this, NormalizePeer(peerBaseCallsign));

    private InboundPartial? Load(string peer, string mid)
    {
        string path = PathFor(peer, mid);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] raw = File.ReadAllBytes(path);
                return Decode(raw, mid);
            }
            catch (IOException)
            {
                return null; // unreadable/torn (shouldn't happen given atomic writes) — treat as absent
            }
        }
    }

    private void Save(string peer, string mid, ReadOnlySpan<byte> compressed, int expectedCompressedSize)
    {
        byte[] encoded = Encode(mid, compressed, expectedCompressedSize, _time.GetUtcNow().ToUnixTimeSeconds());
        string path = PathFor(peer, mid);
        string tmp = path + ".tmp";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(encoded, 0, encoded.Length);
                fs.Flush(flushToDisk: true); // the durability boundary: bytes are on disk before the rename
            }

            File.Move(tmp, path, overwrite: true); // atomic publish — a crash leaves old-or-new, never torn
        }
    }

    private void Discard(string peer, string mid)
    {
        string path = PathFor(peer, mid);
        lock (_gate)
        {
            TryDelete(path);
            TryDelete(path + ".tmp");
        }
    }

    /// <summary>
    /// Deletes partials older than the TTL (and any orphaned <c>.tmp</c> files from an interrupted
    /// write). Returns the count removed. Call at startup and periodically.
    /// </summary>
    public int CollectStale()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            long cutoffUnix = (_time.GetUtcNow() - _ttl).ToUnixTimeSeconds();
            int removed = 0;
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    // Orphaned .tmp (an interrupted write) is always reclaimed; a committed partial is
                    // reclaimed when its STORED save-time (TimeProvider-driven) is older than the TTL.
                    if (file.EndsWith(".tmp", StringComparison.Ordinal) || SavedAtUnix(file) < cutoffUnix)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (IOException)
                {
                    // best effort
                }
            }

            return removed;
        }
    }

    private string PathFor(string peer, string mid) => Path.Combine(_root, peer, Hash(mid) + ".partial");

    private static string NormalizePeer(string call)
    {
        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(call ?? ""));
        // Keep only filename-safe characters; a callsign is already [A-Z0-9] but be defensive about
        // an unknown TCP login string reaching here as a "peer".
        var sb = new StringBuilder(baseCall.Length);
        foreach (char c in baseCall)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static string Hash(string mid)
    {
        // The mid (BID/MID) is short and mostly filename-safe, but '/' and case can appear; hash it
        // to a stable, collision-resistant, filename-safe token. The mid itself is also stored inside
        // the file for self-description.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(mid.ToUpperInvariant()));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    // Header: Magic(8) · savedAtUnix(int64) · expectedCompressedSize(int32) · midLen(int32).
    private const int HeaderFixedLength = 8 + 8 + 4 + 4;

    private static byte[] Encode(string mid, ReadOnlySpan<byte> compressed, int expectedCompressedSize, long savedAtUnix)
    {
        byte[] midBytes = Encoding.UTF8.GetBytes(mid);
        var buffer = new byte[HeaderFixedLength + midBytes.Length + compressed.Length];
        var span = buffer.AsSpan();
        Magic.CopyTo(span);
        int w = Magic.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[w..], savedAtUnix);
        w += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[w..], expectedCompressedSize);
        w += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[w..], midBytes.Length);
        w += 4;
        midBytes.CopyTo(span[w..]);
        w += midBytes.Length;
        compressed.CopyTo(span[w..]);
        return buffer;
    }

    private static InboundPartial? Decode(byte[] raw, string expectedMid)
    {
        var span = raw.AsSpan();
        if (span.Length < HeaderFixedLength || !span[..Magic.Length].SequenceEqual(Magic))
        {
            return null;
        }

        int w = Magic.Length + 8; // skip Magic + savedAtUnix
        int expectedCompressedSize = BinaryPrimitives.ReadInt32LittleEndian(span[w..]);
        w += 4;
        int midLen = BinaryPrimitives.ReadInt32LittleEndian(span[w..]);
        w += 4;
        if (midLen < 0 || w + midLen > span.Length)
        {
            return null;
        }

        string storedMid = Encoding.UTF8.GetString(span.Slice(w, midLen));
        w += midLen;

        // Defensive: a hash collision (16 bytes of SHA-256) is astronomically unlikely, but if the
        // self-described id ever disagrees with the one we keyed by, do not trust the file for resume.
        if (!string.Equals(storedMid, expectedMid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new InboundPartial(span[w..].ToArray(), expectedCompressedSize);
    }

    /// <summary>Reads just the stored save-time (unix seconds) from a partial file; long.MaxValue when unreadable (so a corrupt file is not GC'd by age, only by orphan-.tmp).</summary>
    private static long SavedAtUnix(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[Magic.Length + 8];
            if (fs.Read(head) < head.Length || !head[..Magic.Length].SequenceEqual(Magic))
            {
                return long.MaxValue;
            }

            return BinaryPrimitives.ReadInt64LittleEndian(head[Magic.Length..]);
        }
        catch (IOException)
        {
            return long.MaxValue;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class PeerView(FileInboundPartialStore store, string peer) : IInboundResumeStore
    {
        public InboundPartial? TryLoad(string messageId) => store.Load(peer, messageId);

        public void Save(string messageId, ReadOnlySpan<byte> compressedSoFar, int expectedCompressedSize) =>
            store.Save(peer, messageId, compressedSoFar, expectedCompressedSize);

        public void Discard(string messageId) => store.Discard(peer, messageId);
    }
}
