using Bbs.Import.Bpq;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Tests for the linmail.cfg (libconfig) parser. The BBSUsers caret-field order and the BBSNumber
/// position (field index 10) are verified against GetUserDatabase (BBSUtilities.c:687–740).
/// </summary>
public sealed class LinmailConfigParserTests
{
    private const string Sample =
        """
        main :
        {
          BBSName = "GB7RDG";
          SYSOPCall = "GB7RDG";
          H-Route = "#42.GBR.EURO";
        };
        BBSForwarding :
        {
          GB7CIP :
          {
            TOCalls = "";
            ConnectScript = "INTERLOCK 3|C 3 !GB7WEM-7|C uhf gb7cip";
            ATCalls = "GBR|WW|NOAM|EU";
            HRoutesP = "#32.GBR.EURO|GBR.EURO";
            Enabled = 1;
            UseB2Protocol = 1;
            FwdInterval = 720;
            ConTimeout = 120;
            BBSHA = "GB7CIP.#32.GBR.EURO";
          };
        };
        Housekeeping :
        {
          MaxMsgno = 60000;
          BidLifetime = 60;
          MaxAge = 365;
        };
        BBSUsers :
        {
          M0LTE = "Tom^addr^GB7RDG^IO91^secret^RG1 1AA^^29^0^0^0^0^0^1781734234^stats^last";
          GB7CIP = "BBS^^^^^^^0^16^0^5^0^0^0^^";
        };
        """;

    [Fact]
    public void Parse_Main_ReadsBbsIdentityAndHousekeeping()
    {
        BpqMailConfig cfg = LinmailConfigParser.Parse(Sample);
        Assert.Equal("GB7RDG", cfg.BbsName);
        Assert.Equal("GB7RDG", cfg.SysopCall);
        Assert.Equal("#42.GBR.EURO", cfg.HRoute);
        Assert.Equal(60000, cfg.MaxMsgno);
        Assert.Equal(60, cfg.BidLifetime);
        Assert.Equal(365, cfg.MaxAge);
    }

    [Fact]
    public void Parse_Partner_SplitsPipeFieldsAndFlags()
    {
        BpqMailConfig cfg = LinmailConfigParser.Parse(Sample);
        BpqPartner p = Assert.Single(cfg.Partners);
        Assert.Equal("GB7CIP", p.Call);
        Assert.Equal(["INTERLOCK 3", "C 3 !GB7WEM-7", "C uhf gb7cip"], p.ConnectScript);
        Assert.Equal(["GBR", "WW", "NOAM", "EU"], p.AtCalls);
        Assert.Equal(["#32.GBR.EURO", "GBR.EURO"], p.HRoutesP);
        Assert.True(p.Enabled);
        Assert.True(p.UseB2);
        Assert.Equal(720, p.FwdInterval);
        Assert.Equal(120, p.ConTimeout);
        Assert.Equal("GB7CIP.#32.GBR.EURO", p.BbsHa);
    }

    [Fact]
    public void Parse_User_FieldOrder_BbsNumberIsFieldTen()
    {
        BpqMailConfig cfg = LinmailConfigParser.Parse(Sample);

        BpqUser tom = cfg.Users.Single(u => u.Call == "M0LTE");
        Assert.Equal("Tom", tom.Name);
        Assert.Equal("GB7RDG", tom.HomeBbs);
        Assert.Equal("IO91", tom.Qra);
        Assert.Equal("RG1 1AA", tom.Zip);
        Assert.Equal(0, tom.Flags);
        Assert.False(tom.IsBbs);
        Assert.Equal(29, tom.LastListed);        // field index 7 (lastmsg)
        Assert.Equal(1781734234, tom.TimeLastConnected);

        BpqUser cip = cfg.Users.Single(u => u.Call == "GB7CIP");
        Assert.Equal("BBS", cip.Name);
        Assert.Equal(16, cip.Flags);     // 0x10 = F_BBS
        Assert.True(cip.IsBbs);
        Assert.Equal(5, cip.BbsNumber);  // field index 10
    }

    [Fact]
    public void Parse_StarPrefixedDigitCall_IsStripped()
    {
        const string cfg =
            """
            BBSUsers :
            {
              *2E0ABC = "Bob^^^^^^^0^0^0^0^0^0^0^^";
            };
            """;
        BpqMailConfig parsed = LinmailConfigParser.Parse(cfg);
        Assert.Equal("2E0ABC", Assert.Single(parsed.Users).Call);
    }

    [Fact]
    public void Parse_RealStaleSnapshot_ManyPartnersAndUsers()
    {
        if (!Fixtures.HasGb7rdgSnapshot)
        {
            return;
        }

        BpqMailConfig cfg = LinmailConfigParser.Read(Fixtures.Gb7rdgLinmail());
        Assert.Equal("GB7RDG", cfg.BbsName);
        Assert.True(cfg.Partners.Count >= 20);
        Assert.True(cfg.Users.Count(u => u.IsBbs && u.BbsNumber > 0) >= 10);
    }

    [Fact]
    public void Parse_Bpq32Cfg_ReadsNodeAndBbsApplication()
    {
        const string bpq32 =
            """
            NODECALL=GB7RDG         ; Node callsign
            APPLICATION 1,BBS,,GB7RDG-2,RDGBBS,255
            APPLICATION 2,CHAT,,GB7RDG-1,RDGCHT,255
            """;
        Bpq32NodeInfo info = Bpq32CfgParser.Parse(bpq32);
        Assert.Equal("GB7RDG", info.NodeCall);
        Assert.Equal("GB7RDG-2", info.BbsCall);
        Assert.Equal("RDGBBS", info.BbsAlias);
    }
}
