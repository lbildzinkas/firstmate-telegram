using FirstmateTelegram.Infrastructure;
using Telegram.Bot;

namespace FirstmateTelegram.Telegram;

/// <summary>Builds Telegram.Bot clients. Tests point <see cref="BaseUrl"/> at a fake Bot API server.</summary>
public sealed class TelegramClientFactory
{
    public TelegramClientFactory(string? baseUrl = null) => BaseUrl = baseUrl;

    public string? BaseUrl { get; }

    public ITelegramBotClient Create(string token)
    {
        TelegramBotClientOptions options;
        try
        {
            options = new TelegramBotClientOptions(token, BaseUrl) { RetryCount = 0 };
        }
        catch (ArgumentException exception)
        {
            throw new BridgeException("The saved bot token is not in Telegram's token format. Run `firstmate-telegram setup` to save a new one.", exception);
        }

        // Per-call deadlines come from the gateway, so the client itself never gives up on a long poll.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new TelegramBotClient(options, http);
    }

    public static bool LooksLikeToken(string token)
    {
        try
        {
            _ = new TelegramBotClientOptions(token);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
