using Bbs.Host;

// pdn-bbs — the deployable BBS app package (design.md src/Bbs.Host). All composition lives
// in HostComposition.Build so the tests can boot the exact production wiring.

WebApplication app = HostComposition.Build(args);
app.Run();
