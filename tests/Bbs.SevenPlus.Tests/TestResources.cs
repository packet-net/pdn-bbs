using System.Reflection;

namespace Bbs.SevenPlus.Tests;

/// <summary>
/// Loads the embedded golden-vector fixtures (reference sample-data and the BBS
/// mail dump — see Resources/PROVENANCE.txt).
/// </summary>
internal static class TestResources
{
    private static readonly Assembly Asm = typeof(TestResources).Assembly;
    private const string Prefix = "Bbs.SevenPlus.Tests.Resources.";

    public static byte[] Bytes(string name)
    {
        using var stream = Asm.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException(
                $"embedded resource '{name}' not found. Available: {string.Join(", ", Asm.GetManifestResourceNames())}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The original fields.jpg (the decode target).</summary>
    public static byte[] FieldsJpg() => Bytes("fields.jpg");

    /// <summary>All 17 encoded parts of fields.jpg, in order p01..p11 (hex part numbers).</summary>
    public static IReadOnlyList<byte[]> FieldsParts()
    {
        var parts = new List<byte[]>(17);
        for (var i = 1; i <= 17; i++)
        {
            parts.Add(Bytes($"fields.p{i:x2}"));
        }

        return parts;
    }

    /// <summary>The multi-file BBS mail dump (25 parts of four files).</summary>
    public static byte[] BbsMail() => Bytes("bbs-mail.txt");
}
