using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

/// <summary>
/// The dashboard-health verdict for a forwarding cycle (<see cref="ForwardingScheduler.ClassifyHealth"/>):
/// "ok" only when the cycle made forward progress, "failing" when it couldn't connect OR ran but left
/// every queued message still queued (the connect-but-never-deliver case).
/// </summary>
public class ForwardingSchedulerHealthTests
{
    [Fact]
    public void CouldNotConnect_IsFailing_WithTheReason()
    {
        Assert.Equal((false, "connect refused"), ForwardingScheduler.ClassifyHealth(ran: false, "connect refused", 3, 3));
    }

    [Fact]
    public void CouldNotConnect_WithNoReason_GetsAGenericOne()
    {
        (bool ok, string error) = ForwardingScheduler.ClassifyHealth(ran: false, null, 3, 3);
        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EmptyQueuePoll_IsOk()
    {
        Assert.Equal((true, ""), ForwardingScheduler.ClassifyHealth(ran: true, null, queuedAtStart: 0, queuedAfter: 0));
    }

    [Theory]
    [InlineData(3, 0)] // delivered all
    [InlineData(3, 1)] // delivered some (or deferred/held)
    public void QueueShrank_IsOk(int start, int after)
    {
        Assert.Equal((true, ""), ForwardingScheduler.ClassifyHealth(ran: true, null, start, after));
    }

    [Fact]
    public void RanButDeliveredNothing_IsFailing_NotAQuietlyStuckOk()
    {
        (bool ok, string error) = ForwardingScheduler.ClassifyHealth(ran: true, null, queuedAtStart: 2, queuedAfter: 2);
        Assert.False(ok);
        Assert.Contains("delivered nothing", error, StringComparison.Ordinal);
        Assert.Contains("2 messages", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RanButDeliveredNothing_SingleMessage_ReadsSingular()
    {
        (_, string error) = ForwardingScheduler.ClassifyHealth(ran: true, null, queuedAtStart: 1, queuedAfter: 1);
        Assert.Contains("1 message still queued", error, StringComparison.Ordinal);
    }
}
