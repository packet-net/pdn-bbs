using Bbs.Core;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// Consumes White Pages ("WP", the network directory) update messages into the directory store
/// (issue #36). A WP update is an ordinary FBB/B2F message addressed to the reserved pseudo-call
/// <c>WP</c> whose body is one <c>On …</c> record per line; see <see cref="WhitePagesParser"/> for
/// the wire format. This is the harvest half — it parses the body and date-wins-upserts each record
/// into <c>whitepages</c>, kept entirely separate from the mail store. The DISPOSITION of the source
/// message (consume-personal-to-us vs keep-and-forward-transit) is the caller's
/// (<see cref="InboundMessageReceiver"/>) decision; this consumer only harvests.
/// </summary>
public sealed class WhitePagesConsumer
{
    private readonly BbsStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the consumer over the directory store.</summary>
    public WhitePagesConsumer(BbsStore store, ILogger<WhitePagesConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Parses a WP-update body and upserts every valid record into the directory. Returns the number
    /// of records PARSED from the body (the recognition guard's "≥1 record" check uses this; not every
    /// parsed record necessarily changes a field — a stale or identical re-ingest is a no-op upsert).
    /// Malformed/short/non-record lines are skipped silently inside the parser.
    /// </summary>
    public int Ingest(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        IReadOnlyList<WhitePagesRecord> records = WhitePagesParser.Parse(body);
        int changed = 0;
        foreach (WhitePagesRecord record in records)
        {
            if (_store.UpsertWhitePages(record, source: "wp"))
            {
                changed++;
            }
        }

        if (records.Count > 0)
        {
            LogIngested(_logger, records.Count, changed, null);
        }

        return records.Count;
    }

    private static readonly Action<ILogger, int, int, Exception?> LogIngested =
        LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(1, "WhitePagesIngested"),
            "White Pages update consumed: {Parsed} records parsed, {Changed} directory entries changed");
}
