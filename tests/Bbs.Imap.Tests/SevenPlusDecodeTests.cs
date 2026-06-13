using System.Text;
using Bbs.Core;
using Bbs.Imap;
using Bbs.SevenPlus;
using MimeKit;

namespace Bbs.Imap.Tests;

/// <summary>
/// The render-time 7plus decode: a message whose body carries a complete inline 7plus file is
/// surfaced (via <see cref="ImapRenderedMessage"/>) as a real decoded attachment, so a client sees
/// the file rather than the raw code-lines.
/// </summary>
public sealed class SevenPlusDecodeTests
{
    [Fact]
    public void InlineCompleteSevenPlusFile_SurfacesAsDecodedAttachment()
    {
        // A binary payload including bytes that are legitimate 7plus alphabet symbols (0x85) — the
        // round-trip must be byte-exact.
        byte[] original = [.. Enumerable.Range(0, 600).Select(i => (byte)((i * 31 + 0x85) & 0xFF))];
        string body = SevenPlusEncoder.Encode(original, "PHOTO.JPG")[0]; // single part for a small file

        using var test = new TestStore();
        Message m = test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "here is an image", body: body));

        ImapRenderedMessage rendered = ImapRenderedMessage.Render(test.Store.GetMessage(m.Number)!);

        MimePart attachment = Assert.IsAssignableFrom<MimePart>(Assert.Single(rendered.Mime.Attachments));
        Assert.Equal("PHOTO.JPG", attachment.FileName);
        Assert.NotNull(attachment.Content);
        using var ms = new MemoryStream();
        attachment.Content.DecodeTo(ms);
        Assert.Equal(original, ms.ToArray()); // byte-exact decode
    }

    [Fact]
    public void DecodedAttachments_DirectApi_ReturnsTheFile()
    {
        byte[] original = [.. Enumerable.Range(0, 200).Select(i => (byte)i)];
        string body = SevenPlusEncoder.Encode(original, "DATA.BIN")[0];

        using var test = new TestStore();
        Message m = test.Store.AddMessage(Drafts.Bulletin(body: body));

        IReadOnlyList<MessageAttachment> decoded = SevenPlusDecode.DecodedAttachments(test.Store.GetMessage(m.Number)!);
        MessageAttachment file = Assert.Single(decoded);
        Assert.Equal("DATA.BIN", file.Name);
        Assert.Equal(original, file.Content.ToArray());
    }

    [Fact]
    public void NonSevenPlusBody_AddsNoAttachment()
    {
        using var test = new TestStore();
        Message m = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "just prose", body: "hello world\r\nno 7plus here\r"));

        ImapRenderedMessage rendered = ImapRenderedMessage.Render(test.Store.GetMessage(m.Number)!);
        Assert.Empty(rendered.Mime.Attachments);
        Assert.Empty(SevenPlusDecode.DecodedAttachments(test.Store.GetMessage(m.Number)!));
    }

    [Fact]
    public void DecodedInlineFile_StripsTheRawSevenPlusBlockFromTheTextBody()
    {
        // When the inline 7plus file is surfaced as a decoded attachment, the rendered text body must
        // keep the surrounding prose but DROP the raw go_7+/code/stop_7+ wall (Tom: "it looks a mess").
        byte[] original = [.. Enumerable.Range(0, 400).Select(i => (byte)(i * 7))];
        string encoded = SevenPlusEncoder.Encode(original, "SNAP.JPG")[0];
        string body = "Here is a photo for you.\r\n\r\n" + encoded + "\r\n\r\n73 de M0LTE\r\n";

        using var test = new TestStore();
        Message m = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "photo", body: body));

        ImapRenderedMessage rendered = ImapRenderedMessage.Render(test.Store.GetMessage(m.Number)!);

        // The decoded file is present as an attachment.
        Assert.Equal("SNAP.JPG", Assert.IsAssignableFrom<MimePart>(Assert.Single(rendered.Mime.Attachments)).FileName);

        // The text body keeps the prose and drops the 7plus block entirely.
        string text = rendered.Mime.TextBody ?? string.Empty;
        Assert.Contains("Here is a photo for you.", text, StringComparison.Ordinal);
        Assert.Contains("73 de M0LTE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("go_7+", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stop_7+", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StripInlineSevenPlus_RemovesBlock_KeepsProse_NoOpWithoutMarkers()
    {
        byte[] original = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
        string encoded = SevenPlusEncoder.Encode(original, "F.BIN")[0];

        string stripped = SevenPlusDecode.StripInlineSevenPlus("before\r\n" + encoded + "\r\nafter\r\n");
        Assert.Contains("before", stripped, StringComparison.Ordinal);
        Assert.Contains("after", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("go_7+", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("stop_7+", stripped, StringComparison.Ordinal);

        // A body with no 7plus markers is returned unchanged.
        const string plain = "just some text\r\nover two lines";
        Assert.Equal(plain, SevenPlusDecode.StripInlineSevenPlus(plain));
    }

    [Fact]
    public void IncompletePartOfMultiPartFile_DoesNotDecodeStandalone()
    {
        // Force two parts, then store only the FIRST — it can't be assembled alone, so no attachment.
        byte[] original = [.. Enumerable.Range(0, 40_000).Select(i => (byte)i)];
        IReadOnlyList<string> parts = SevenPlusEncoder.Encode(original, "BIG.BIN", maxPartBytes: 12_000);
        Assert.True(parts.Count >= 2);

        using var test = new TestStore();
        Message m = test.Store.AddMessage(Drafts.Bulletin(body: parts[0])); // only part 1 of N

        Assert.Empty(SevenPlusDecode.DecodedAttachments(test.Store.GetMessage(m.Number)!));
    }
}
