using Bbs.Core;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Unit tests for the pure addr→(call, at) split + classification + (type, route) grouping that turns the
/// accepted RCPT list into one <c>MessageDraft</c> per distinct (type, At). No server — just the static
/// helper.
/// </summary>
public sealed class SmtpRecipientGroupingTests
{
    [Fact]
    public void Split_BareCallsign_HasNullRoute()
    {
        (string call, string? at) = SmtpRecipientGrouping.Split("M0LTE");
        Assert.Equal("M0LTE", call);
        Assert.Null(at);
    }

    [Fact]
    public void Split_RoutedAddress_SplitsOnFirstAt()
    {
        (string call, string? at) = SmtpRecipientGrouping.Split("M0LTE@GB7RDG.GBR.EURO");
        Assert.Equal("M0LTE", call);
        Assert.Equal("GB7RDG.GBR.EURO", at);
    }

    [Fact]
    public void Group_SingleRecipient_OneGroup()
    {
        IReadOnlyList<SmtpRecipientGroup> groups = SmtpRecipientGrouping.Group(["M0LTE"]);
        SmtpRecipientGroup group = Assert.Single(groups);
        Assert.Null(group.At);
        Assert.Equal(["M0LTE"], group.Calls);
    }

    [Fact]
    public void Group_SameRoute_CollapsesIntoOneGroup()
    {
        IReadOnlyList<SmtpRecipientGroup> groups = SmtpRecipientGrouping.Group(
            ["G0ABC@GB7RDG.GBR.EURO", "G0DEF@GB7RDG.GBR.EURO"]);
        SmtpRecipientGroup group = Assert.Single(groups);
        Assert.Equal("GB7RDG.GBR.EURO", group.At);
        Assert.Equal(["G0ABC", "G0DEF"], group.Calls);
    }

    [Fact]
    public void Group_DistinctRoutes_OneGroupEach_OrderPreserved()
    {
        // All four are callsign-shaped (personals), so the only grouping axis is the route.
        IReadOnlyList<SmtpRecipientGroup> groups = SmtpRecipientGrouping.Group(
            ["G0ABC@GB7RDG.GBR.EURO", "G0XYZ", "G0DEF@GB7RDG.GBR.EURO", "G0LOC"]);

        Assert.Equal(2, groups.Count); // GB7RDG.GBR.EURO group, then the null-route group

        Assert.Equal("GB7RDG.GBR.EURO", groups[0].At);
        Assert.Equal(["G0ABC", "G0DEF"], groups[0].Calls);

        Assert.Null(groups[1].At);
        Assert.Equal(["G0XYZ", "G0LOC"], groups[1].Calls);
    }

    [Fact]
    public void Group_CallsignRecipient_IsPersonal()
    {
        SmtpRecipientGroup group = Assert.Single(SmtpRecipientGrouping.Group(["M0LTE"]));
        Assert.Equal(MessageType.Personal, group.Type);
        Assert.Equal(["M0LTE"], group.Calls);
    }

    [Fact]
    public void Group_NonCallsignToken_IsBulletinCategory()
    {
        SmtpRecipientGroup group = Assert.Single(SmtpRecipientGrouping.Group(["ALL"]));
        Assert.Equal(MessageType.Bulletin, group.Type);
        Assert.Equal(["ALL"], group.Calls);
    }

    [Fact]
    public void Group_PersonalAndBulletin_SplitByType()
    {
        // A single submission to both a callsign and a category yields one Personal group + one Bulletin
        // group, even though both share the (null) route.
        IReadOnlyList<SmtpRecipientGroup> groups = SmtpRecipientGrouping.Group(["M0LTE", "ALL"]);
        Assert.Equal(2, groups.Count);

        SmtpRecipientGroup personal = Assert.Single(groups, g => g.Type == MessageType.Personal);
        Assert.Equal(["M0LTE"], personal.Calls);

        SmtpRecipientGroup bulletin = Assert.Single(groups, g => g.Type == MessageType.Bulletin);
        Assert.Equal(["ALL"], bulletin.Calls);
    }

    [Theory]
    [InlineData("<M0LTE@pdn>", "M0LTE@pdn")]
    [InlineData("<M0LTE@pdn> SIZE=1234", "M0LTE@pdn")]
    [InlineData("M0LTE@pdn", "M0LTE@pdn")]
    [InlineData("<>", null)]
    [InlineData("", null)]
    public void ExtractAddrSpec_PullsTheAddress(string input, string? expected)
    {
        Assert.Equal(expected, SmtpSession.ExtractAddrSpec(input));
    }
}
