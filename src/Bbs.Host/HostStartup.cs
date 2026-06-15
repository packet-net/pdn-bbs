using Bbs.Core;

namespace Bbs.Host;

/// <summary>Startup composition steps shared between Program and the tests.</summary>
public static class HostStartup
{
    /// <summary>
    /// Seeds the store's partner table from <c>bbs.yaml</c> <c>partners:</c> — <b>store-first</b>:
    /// the SQLite partners table is the source of truth, edited live via the forwarding editor (UI
    /// or YAML). The config file is a first-boot SEED only: its partners are imported ONLY when the
    /// store has no partners yet. Once seeded, this never re-imports or deletes — so editor changes
    /// persist across restarts and a partner removed in the editor does not reappear from the file
    /// (and a partner added in the editor is not clobbered by the file).
    /// </summary>
    public static void SyncPartners(BbsStore store, BbsHostConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        // Store-first: only seed an empty store. A populated store is authoritative — leave it alone.
        if (store.ListPartners().Count > 0)
        {
            return;
        }

        foreach (PartnerConfig partner in config.Partners)
        {
            if (!string.IsNullOrWhiteSpace(partner.Call))
            {
                store.UpsertPartner(partner.ToPartner());
            }
        }
    }
}
