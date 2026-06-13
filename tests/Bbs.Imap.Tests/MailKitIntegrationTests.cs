using Bbs.Core;
using Bbs.Mime;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace Bbs.Imap.Tests;

/// <summary>
/// End-to-end tests driving a real <see cref="ImapServer"/> with a real MailKit <see cref="ImapClient"/>
/// over loopback — MailKit is the strict correctness oracle (it throws on any malformed response). These
/// run in normal CI (in-process, loopback; like the host's WebmailTests) — not Interop.
/// </summary>
public sealed class MailKitIntegrationTests
{
    private const string MailDomain = "pdn";

    [Fact]
    public async Task Connect_GreetsAndAdvertisesImap4Rev1()
    {
        using var test = new TestStore();
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        Assert.True(client.IsConnected);
        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.IMAP4rev1));

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Authenticate_SucceedsWithRightPassword_FailsWithWrong()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "correct horse battery");
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using (ImapClient ok = await harness.ConnectAsync())
        {
            await ok.AuthenticateAsync("M0LTE", "correct horse battery");
            Assert.True(ok.IsAuthenticated);
            await ok.DisconnectAsync(quit: true);
        }

        using ImapClient bad = await harness.ConnectAsync();
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(
            () => bad.AuthenticateAsync("M0LTE", "wrong password"));
        await bad.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task GetFolders_ShowsInboxAndBulletinCategories()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase one");
        test.Store.AddMessage(Drafts.Bulletin(to: "NEWS", subject: "n"));
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "a"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase one");

        Assert.NotNull(client.Inbox);
        IList<IMailFolder> all = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        var names = all.Select(f => f.FullName).ToList();
        Assert.Contains("Bulletins/NEWS", names);
        Assert.Contains("Bulletins/ALL", names);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task OpenInbox_CountsOnlyPersonalsToThisCall_HidesOthersAndSevenPlus()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase two");
        test.Store.AddMessage(Drafts.Personal(from: "G0AAA", to: "M0LTE", subject: "mine 1"));
        test.Store.AddMessage(Drafts.Personal(from: "G0BBB", to: "M0LTE", subject: "mine 2"));
        test.Store.AddMessage(Drafts.Personal(from: "G0CCC", to: "G8ZZZ", subject: "not mine"));
        Message part = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "7p part"));
        test.Store.RecordSevenPlusPart("k", "F.JPG", 100, 2, 5, 1, part.Number);
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase two");
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        Assert.Equal(2, client.Inbox.Count);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Fetch_DecodesSubjectFromToDateAndUid()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase three");
        Message stored = test.Store.AddMessage(
            Drafts.Personal(from: "G0AAA", to: "M0LTE", at: "GB7RDG", subject: "Hello there"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase three");
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        IList<IMessageSummary> summaries = await client.Inbox.FetchAsync(
            0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags);
        IMessageSummary summary = Assert.Single(summaries);

        Assert.Equal((uint)stored.Number, summary.UniqueId.Id);
        Assert.NotNull(summary.Envelope);
        Assert.Equal("Hello there", summary.Envelope.Subject);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero), summary.Envelope.Date);

        // From / To round-trip back through the codec to the original packet addresses.
        MailboxAddress from = Assert.IsType<MailboxAddress>(summary.Envelope.From[0]);
        Assert.True(PacketAddressCodec.TryDecode(from.Address, MailDomain, out string fromPacket));
        Assert.Equal("G0AAA", fromPacket);

        MailboxAddress to = Assert.IsType<MailboxAddress>(summary.Envelope.To[0]);
        Assert.True(PacketAddressCodec.TryDecode(to.Address, MailDomain, out string toPacket));
        Assert.Equal("M0LTE@GB7RDG", toPacket);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Fetch_FlagsReflectReadState()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase four");
        Message unread = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "unread"));
        Message read = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "read"));
        test.Store.MarkRead(read.Number, "M0LTE");
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase four");
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        IList<IMessageSummary> summaries = await client.Inbox.FetchAsync(
            0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);

        IMessageSummary unreadSummary = summaries.Single(s => s.UniqueId.Id == (uint)unread.Number);
        IMessageSummary readSummary = summaries.Single(s => s.UniqueId.Id == (uint)read.Number);
        Assert.False(unreadSummary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.True(readSummary.Flags!.Value.HasFlag(MessageFlags.Seen));

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task PeekBody_DoesNotMarkSeen()
    {
        // MailKit's GetMessage uses BODY.PEEK[] (it controls \Seen explicitly), so a peek must NOT
        // mark the message read in the store.
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase five");
        Message msg = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "peek me", body: "the body\r"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase five");
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);

        MimeMessage fetched = await client.Inbox.GetMessageAsync(new UniqueId((uint)msg.Number));
        Assert.Contains("the body", fetched.TextBody, StringComparison.Ordinal);

        Message after = test.Store.GetMessage(msg.Number)!;
        Assert.Null(after.Recipients.Single(r => Callsigns.BaseEquals(r.ToCall, "M0LTE")).ReadAt);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task NonPeekBodyFetch_MarksMessageSeen_InStoreAndOnReFetch()
    {
        // A non-PEEK BODY[] fetch sets \Seen (RFC 3501 §6.4.5). Driven over a raw client so we issue
        // the non-peek form (MailKit's high-level API always peeks).
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase 5b");
        Message msg = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "read me", body: "the body\r"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawImapClient.ConnectAsync(harness.Port);
        await raw.CommandAsync("a1", "LOGIN M0LTE \"passphrase 5b\"");
        await raw.CommandAsync("a2", "SELECT INBOX");

        string fetch = await raw.CommandAsync("a3", $"FETCH 1 BODY[]");
        Assert.Contains("the body", fetch, StringComparison.Ordinal);
        Assert.Contains("\\Seen", fetch, StringComparison.Ordinal); // the flag-change is reported

        // The store recorded the read.
        Message after = test.Store.GetMessage(msg.Number)!;
        Assert.NotNull(after.Recipients.Single(r => Callsigns.BaseEquals(r.ToCall, "M0LTE")).ReadAt);

        // A re-fetch of FLAGS shows \Seen.
        string flags = await raw.CommandAsync("a4", "FETCH 1 (FLAGS)");
        Assert.Contains("\\Seen", flags, StringComparison.Ordinal);

        await raw.CommandAsync("a5", "LOGOUT");
    }

    [Fact]
    public async Task AddFlagsSeen_MarksReadInStore()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase six");
        Message msg = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "flag me"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase six");
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);

        await client.Inbox.AddFlagsAsync(new UniqueId((uint)msg.Number), MessageFlags.Seen, silent: false);

        Message after = test.Store.GetMessage(msg.Number)!;
        MessageRecipient recipient = after.Recipients.Single(r => Callsigns.BaseEquals(r.ToCall, "M0LTE"));
        Assert.NotNull(recipient.ReadAt);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Bulletins_TrackPerUserReadState()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase seven");
        test.Store.AddMessage(Drafts.Bulletin(to: "NEWS", subject: "newsflash"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase seven");

        // A fresh bulletin is unseen for this user.
        IMailFolder news = await client.GetFolderAsync("Bulletins/NEWS");
        await news.OpenAsync(FolderAccess.ReadWrite);
        IList<IMessageSummary> before = await news.FetchAsync(0, -1, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId);
        Assert.False(before.Single().Flags!.Value.HasFlag(MessageFlags.Seen));

        // Marking it seen persists in the store (per-user).
        await news.AddFlagsAsync([before.Single().UniqueId], MessageFlags.Seen, silent: true);
        Assert.True(test.Store.IsReadByUser("M0LTE", (long)before.Single().UniqueId.Id));

        // A fresh open shows it seen now.
        await news.CloseAsync();
        await news.OpenAsync(FolderAccess.ReadOnly);
        IList<IMessageSummary> after = await news.FetchAsync(0, -1, MessageSummaryItems.Flags);
        Assert.True(after.Single().Flags!.Value.HasFlag(MessageFlags.Seen));

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Body_AndAttachment_RoundTripByteExact()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase eight");
        byte[] attachmentBytes = [0x01, 0x02, 0x03, 0xFF, 0x00, 0x42, 0xAB];
        var draft = Drafts.Personal(to: "M0LTE", subject: "with attachment", body: "see attached\r") with
        {
            Attachments = [new MessageAttachment("data.bin", attachmentBytes)],
        };
        Message msg = test.Store.AddMessage(draft);
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase eight");
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        // BODYSTRUCTURE parses as a multipart with two children (MailKit throws on a malformed one).
        IList<IMessageSummary> structures = await client.Inbox.FetchAsync(
            0, -1, MessageSummaryItems.BodyStructure | MessageSummaryItems.Size);
        IMessageSummary structure = Assert.Single(structures);
        BodyPartMultipart multipart = Assert.IsType<BodyPartMultipart>(structure.Body);
        Assert.Equal(2, multipart.BodyParts.Count);
        Assert.True(structure.Size > 0);

        MimeMessage fetched = await client.Inbox.GetMessageAsync(new UniqueId((uint)msg.Number));
        Assert.Contains("see attached", fetched.TextBody, StringComparison.Ordinal);

        MimePart attachment = Assert.IsAssignableFrom<MimePart>(fetched.Attachments.Single());
        Assert.Equal("data.bin", attachment.FileName);
        Assert.NotNull(attachment.Content);
        using var stream = new MemoryStream();
        attachment.Content.DecodeTo(stream);
        Assert.Equal(attachmentBytes, stream.ToArray());

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Status_ReportsCountsForABulletinCategory()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase nine");
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "b1"));
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "b2"));
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase nine");
        IMailFolder all = await client.GetFolderAsync("Bulletins/ALL");

        await all.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidNext | StatusItems.UidValidity);
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Unread); // both bulletins unread for this user (per-user read-state)
        Assert.Equal(1u, all.UidValidity);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Logout_IsClean()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "passphrase ten");
        await using ImapServerHarness harness = await ImapServerHarness.StartAsync(test.Store, test.Time);

        using ImapClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "passphrase ten");
        await client.DisconnectAsync(quit: true);
        Assert.False(client.IsConnected);
    }
}
