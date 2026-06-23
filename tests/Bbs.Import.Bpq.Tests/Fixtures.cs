using System.Runtime.CompilerServices;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Locates the on-disk BPQ fixtures: the CONSISTENT oracle state shipped in the repo
/// (docker/oracle/state — the primary test dataset) and, when present on the dev box, the
/// REAL-but-STALE gb7rdg snapshot (used only for parser-robustness assertions).
/// </summary>
internal static class Fixtures
{
    /// <summary>The repo root, located by walking up from this source file to the directory holding pdn-bbs.slnx.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The consistent oracle BPQ dump directory (the primary fixture).</summary>
    public static string OracleStateDir => Path.Combine(RepoRoot, "docker", "oracle", "state");

    public static string OracleDirmes() => Path.Combine(OracleStateDir, "DIRMES.SYS");

    /// <summary>The real-but-stale gb7rdg snapshot directory (dev-box only).</summary>
    public static string Gb7rdgDir { get; } = "/home/tf/gb7rdg-config/bpq";

    public static bool HasOracleState => File.Exists(OracleDirmes());

    public static bool HasGb7rdgSnapshot => File.Exists(Gb7rdgDirmes());

    public static string Gb7rdgDirmes() => Path.Combine(Gb7rdgDir, "DIRMES.SYS");

    public static string Gb7rdgWfbid() => Path.Combine(Gb7rdgDir, "WFBID.SYS");

    public static string Gb7rdgLinmail() => Path.Combine(Gb7rdgDir, "linmail.cfg");

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdn-bbs.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repo root (pdn-bbs.slnx) from the test source path.");
    }
}
