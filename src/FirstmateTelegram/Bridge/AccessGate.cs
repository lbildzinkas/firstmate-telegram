using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FirstmateTelegram.Bridge;

/// <summary>
/// Admits a message only from the paired user in their private chat with the bot. Everything else is dropped
/// silently, with no reply and no reaction, and logged by the sender's numeric id alone.
/// </summary>
public sealed class AccessGate
{
    readonly long _allowedUserId;
    readonly ILogger<AccessGate> _logger;

    public AccessGate(long allowedUserId, ILogger<AccessGate> logger)
    {
        _allowedUserId = allowedUserId;
        _logger = logger;
    }

    /// <summary>The one Telegram account, and private chat, the bridge answers; its chat is where alerts go.</summary>
    public long AllowedUserId => _allowedUserId;

    public bool Admit(Update update)
    {
        if (update.Message is { From.Id: var fromId, Chat: { Id: var chatId, Type: ChatType.Private } }
            && fromId == _allowedUserId
            && chatId == _allowedUserId)
            return true;

        if (update.Message?.From?.Id is { } senderId)
            _logger.LogInformation("ignored a message from user {SenderId}", senderId);
        else
            _logger.LogInformation("ignored an update without a sender");
        return false;
    }
}
