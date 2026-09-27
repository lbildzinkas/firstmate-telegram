namespace FirstmateTelegram.Tests.Support;

/// <summary>
/// Fake, token-shaped strings assembled at run time from parts that are not secret-shaped on their
/// own. Secret scanners flag committed literals that match a provider token pattern even when they
/// are obvious fakes, so tests never spell a full token out; they call this helper instead.
/// </summary>
public static class FakeTokens
{
    /// <summary>The standard fake bot token: the fake server's bot id, a colon, then url-safe characters.</summary>
    public static string Telegram() => Telegram(123456789, "AAHfake", "TokenForTestsOnly", "-0123456789ab");

    /// <summary>A second token-shaped string the fake server rejects, as a revoked token would be.</summary>
    public static string RevokedTelegram() => Telegram(123456789, "AAHrevoked", "TokenForTests", "-0000000000");

    /// <summary>Assembles <c>&lt;bot id&gt;:&lt;tail&gt;</c> so no committed literal is token-shaped by itself.</summary>
    public static string Telegram(long botId, params string[] tail) => botId + ":" + string.Concat(tail);
}
