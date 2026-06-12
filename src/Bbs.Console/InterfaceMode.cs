namespace Bbs.Console;

/// <summary>
/// Which command surface a console session presents (the plain-language mandate, design.md
/// "The plain-language mandate"). <see cref="Plain"/> is the default: a sentence-driven,
/// word-command surface a TNC2 user can drive without W0RLI folklore. <see cref="Classic"/>
/// is the byte-exact terse W0RLI/FBB surface kept whole for users whose automated clients
/// pattern-match the legacy prompts. A user flips it with the typed <c>classic</c>/<c>plain</c>
/// commands (or the sysop sets it); the session engine picks the surface by callsign at
/// connect. Partner-BBS forwarding is a wire property and is unaffected by this setting.
/// </summary>
public enum InterfaceMode
{
    /// <summary>The plain-language, sentence-driven surface (the default per the mandate).</summary>
    Plain,

    /// <summary>The byte-exact terse W0RLI/FBB command surface (compat spec §1), kept whole.</summary>
    Classic,
}
