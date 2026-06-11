using Bbs.Core;

namespace Bbs.Host;

/// <summary>Startup composition steps shared between Program and the tests.</summary>
public static class HostStartup
{
    /// <summary>
    /// Makes the store's partner table match the config (config is the source of truth,
    /// v1): every configured partner is upserted; store partners absent from the config
    /// are deleted (their pending forward-queue rows persist until their messages purge —
    /// <see cref="BbsStore.DeletePartner"/> semantics).
    /// </summary>
    public static void SyncPartners(BbsStore store, BbsHostConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PartnerConfig partner in config.Partners)
        {
            if (string.IsNullOrWhiteSpace(partner.Call))
            {
                continue;
            }

            store.UpsertPartner(partner.ToPartner());
            configured.Add(Callsigns.Normalize(partner.Call));
        }

        foreach (Partner stale in store.ListPartners())
        {
            if (!configured.Contains(Callsigns.Normalize(stale.Call)))
            {
                store.DeletePartner(stale.Call);
            }
        }
    }
}
