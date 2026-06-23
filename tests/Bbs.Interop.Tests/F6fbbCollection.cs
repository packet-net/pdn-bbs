namespace Bbs.Interop.Tests;

/// <summary>
/// Serialises the real-F6FBB tests. Every test here binds the SAME host UDP port (10093 — the
/// port the VM's ax25ipd route targets back at the host, 192.168.76.1:10093), and there is one
/// xfbbd answering one AX.25 address pair, so two of these can never run concurrently. xunit
/// runs the classes within a single collection serially; this collection has no shared fixture
/// (the VM is brought up out-of-band by run/run-vm.sh), only the singleton AXUDP port + daemon.
/// </summary>
[CollectionDefinition(Name)]
public class F6fbbCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "F6fbb";
}
