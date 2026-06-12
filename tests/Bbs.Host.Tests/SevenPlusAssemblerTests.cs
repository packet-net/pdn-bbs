using System.Reflection;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.SevenPlus;

namespace Bbs.Host.Tests;

/// <summary>
/// The inbound 7plus integration end-to-end (design.md "abstract 7plus away from the user"): part-
/// bulletins arrive as separate inbound messages, trickling in over time and out of order; nothing
/// surfaces until the set completes; on completion a synthesized <see cref="Message.LocalOnly"/>
/// message appears carrying the decoded file as a byte-exact attachment; the raw part-messages are
/// hidden from the webmail listing but still forward; the synthesized message is never forwarded.
///
/// Driven through the real <see cref="Bbs.Host.Forwarding.InboundMessageReceiver.Deliver"/> path so
/// the hook fires exactly where production wires it (after StoreAndRoute). The 7plus parts are the
/// reference fields.jpg golden vector — encoded at test time with <see cref="SevenPlusEncoder"/>,
/// which <c>GoldenVectorTests</c> proves reproduces the reference parts byte-for-byte.
/// </summary>
public sealed class SevenPlusAssemblerTests
{
    private const string FromCall = "M0XYZ";
    private const string PartnerCall = "GB7BPQ";

    /// <summary>The reference fields.jpg payload (embedded; see the csproj resource note).</summary>
    private static byte[] FieldsJpg()
    {
        Assembly asm = typeof(SevenPlusAssemblerTests).Assembly;
        using Stream stream = asm.GetManifestResourceStream("Bbs.Host.Tests.Resources.fields.jpg")
            ?? throw new InvalidOperationException("fields.jpg resource missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Encodes a payload into 7plus parts; each part's text is one inbound message body.</summary>
    private static List<string> EncodeParts(byte[] payload, string fileName, long timestamp = 0x5C906C09)
    {
        IReadOnlyList<SevenPlusEncoder.EncodedPart> parts = SevenPlusEncoder.Encode(payload, new SevenPlusEncodeOptions
        {
            FileName = fileName,
            Timestamp = timestamp,
        });
        return parts.Select(p => p.Text).ToList();
    }

    /// <summary>Feeds one part-bulletin inbound through the real receiver (an FA delivery, type B).</summary>
    private static Message DeliverPart(HostHarness host, string partText, string subject, int seq)
    {
        byte[] body = Encoding.Latin1.GetBytes(partText);
        var proposal = new FaProposal('A', 'B', FromCall, "ALL", "ALL", $"{seq}_GB7BPQ", body.Length);
        var delivered = new FbbMessageDelivered(proposal, subject, body);
        return host.Receiver.Deliver(delivered, PartnerCall)!;
    }

    [Fact]
    public async Task FullFile_PartsArriveOutOfOrder_SurfacesByteExactAttachment_OnlyOnCompletion()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = PartnerCall, AtCalls = ["*"] }); // the source partner
        // A SECOND partner gives the bulletins a real onward route (the loop guard blocks forwarding
        // back to the partner a message came from, so the raw parts forward to GB7RDG, not GB7BPQ).
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        byte[] original = FieldsJpg();
        var parts = EncodeParts(original, "fields.jpg");
        Assert.Equal(17, parts.Count); // the reference geometry

        // Deliver every part except one (out of order) — nothing should surface yet.
        var order = Enumerable.Range(0, parts.Count).ToList();
        order.Reverse(); // out of order (17..1)
        int held = order[5]; // hold one back to prove incompleteness blocks surfacing
        order.Remove(held);

        var partMessages = new List<Message>();
        int seq = 1;
        foreach (int i in order)
        {
            partMessages.Add(DeliverPart(host, parts[i], $"fields.p{i + 1:x2}", seq++));
        }

        // Still incomplete: no synthesized local_only message exists; only the raw parts are stored.
        Assert.DoesNotContain(host.Store.ListMessages(new MessageQuery { IncludeHeld = true }), m => m.LocalOnly);
        SevenPlusProgress incomplete = Assert.Single(host.Store.ListIncompleteSevenPlusFiles());
        Assert.Equal(16, incomplete.ReceivedParts);
        Assert.Equal(17, incomplete.TotalParts);
        Assert.Null(incomplete.AssembledMessageNumber);

        // Deliver the last missing part → the set completes and the file surfaces.
        partMessages.Add(DeliverPart(host, parts[held], $"fields.p{held + 1:x2}", seq));

        // Exactly one synthesized local_only message now exists, carrying the decoded file.
        Message synthesized = Assert.Single(
            host.Store.ListMessages(new MessageQuery { IncludeHeld = true }), m => m.LocalOnly);
        Assert.Equal(MessageType.Bulletin, synthesized.Type); // same type as the source part-bulletins
        Assert.Equal(FromCall, synthesized.From);
        Assert.Equal("fields.jpg", synthesized.Subject);
        Assert.Contains("17 parts", synthesized.GetBodyText());

        // The attachment is the original file, byte-for-byte.
        MessageAttachment attachment = Assert.Single(synthesized.Attachments);
        Assert.Equal("fields.jpg", attachment.Name);
        Assert.True(original.AsSpan().SequenceEqual(attachment.Content.Span), "assembled attachment differs from fields.jpg");

        // The file dropped out of the incomplete list (it now lists as the synthesized message).
        Assert.Empty(host.Store.ListIncompleteSevenPlusFiles());

        // Every raw part-message is in the hide-set; the synthesized message is NOT.
        IReadOnlySet<long> hidden = host.Store.GetSevenPlusPartMessageNumbers();
        Assert.Equal(17, hidden.Count);
        foreach (Message part in partMessages)
        {
            Assert.Contains(part.Number, hidden);
        }

        Assert.DoesNotContain(synthesized.Number, hidden);

        // Forward-safety: the synthesized local_only message is never queued for forwarding to any
        // partner (the raw part-bulletins ARE — they forward onward to GB7RDG unchanged).
        IReadOnlyList<Message> rdgQueue = host.Store.GetForwardQueue("GB7RDG");
        Assert.DoesNotContain(rdgQueue, m => m.Number == synthesized.Number);
        Assert.DoesNotContain(host.Store.GetForwardQueue(PartnerCall), m => m.Number == synthesized.Number);
        Assert.Contains(rdgQueue, m => m.Number == partMessages[0].Number);
    }

    [Fact]
    public async Task SinglePartFile_AssemblesImmediately()
    {
        // A tiny payload encodes to a single part; it must surface on that one inbound message.
        await using var host = new HostHarness();
        byte[] original = Encoding.ASCII.GetBytes("Hello, 7plus single part payload!\n");
        var parts = EncodeParts(original, "hello.txt");
        Assert.Single(parts);

        DeliverPart(host, parts[0], "hello.p01", 1);

        Message synthesized = Assert.Single(
            host.Store.ListMessages(new MessageQuery { IncludeHeld = true }), m => m.LocalOnly);
        Assert.Equal("hello.txt", synthesized.Subject);
        MessageAttachment attachment = Assert.Single(synthesized.Attachments);
        Assert.True(original.AsSpan().SequenceEqual(attachment.Content.Span));
        Assert.Empty(host.Store.ListIncompleteSevenPlusFiles());
    }

    [Fact]
    public async Task IncompleteSet_SurfacesOnlyThePlaceholder_NoSynthesizedMessageNoAttachment()
    {
        await using var host = new HostHarness();
        byte[] original = FieldsJpg();
        var parts = EncodeParts(original, "fields.jpg");

        // Deliver only the first three parts.
        for (int i = 0; i < 3; i++)
        {
            DeliverPart(host, parts[i], $"fields.p{i + 1:x2}", i + 1);
        }

        // No synthesized message; the placeholder source shows 3/17.
        Assert.DoesNotContain(host.Store.ListMessages(new MessageQuery { IncludeHeld = true }), m => m.LocalOnly);
        SevenPlusProgress placeholder = Assert.Single(host.Store.ListIncompleteSevenPlusFiles());
        Assert.Equal(3, placeholder.ReceivedParts);
        Assert.Equal(17, placeholder.TotalParts);
        Assert.Equal("fields.jpg", placeholder.HeaderName.Trim().ToLowerInvariant());
        Assert.Equal(MessageType.Bulletin, placeholder.SourceType);
    }

    [Fact]
    public async Task NonSevenPlusMessage_IsUntouched_NoTracking()
    {
        // The cheap path: a normal message without the 7plus magic creates no tracking and is not
        // hidden from listings.
        await using var host = new HostHarness();
        byte[] body = Encoding.Latin1.GetBytes("Just an ordinary bulletin about the antenna party.\r");
        var proposal = new FaProposal('A', 'B', FromCall, "ALL", "ALL", "1_GB7BPQ", body.Length);
        Message stored = host.Receiver.Deliver(new FbbMessageDelivered(proposal, "Antenna party", body), PartnerCall)!;

        Assert.False(stored.LocalOnly);
        Assert.False(host.Store.IsSevenPlusPartMessage(stored.Number));
        Assert.Empty(host.Store.ListIncompleteSevenPlusFiles());
        Assert.Empty(host.Store.GetSevenPlusPartMessageNumbers());
    }

    [Fact]
    public async Task MultiplePartsInOneMessage_AreEachRecorded()
    {
        // ExtractParts can yield several parts from one body; the assembler records each. Two tiny
        // single-part files concatenated into one inbound body → both surface.
        await using var host = new HostHarness();
        byte[] a = Encoding.ASCII.GetBytes("first tiny file\n");
        byte[] b = Encoding.ASCII.GetBytes("second tiny file\n");
        string combined = string.Concat(EncodeParts(a, "a.txt")) + string.Concat(EncodeParts(b, "b.txt"));

        DeliverPart(host, combined, "two files", 1);

        var local = host.Store.ListMessages(new MessageQuery { IncludeHeld = true }).Where(m => m.LocalOnly).ToList();
        Assert.Equal(2, local.Count);
        Assert.Contains(local, m => m.Subject == "a.txt");
        Assert.Contains(local, m => m.Subject == "b.txt");
        Assert.True(a.AsSpan().SequenceEqual(local.Single(m => m.Subject == "a.txt").Attachments[0].Content.Span));
        Assert.True(b.AsSpan().SequenceEqual(local.Single(m => m.Subject == "b.txt").Attachments[0].Content.Span));
    }
}
