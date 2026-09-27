using System.Globalization;
using FirstmateTelegram.Infrastructure;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FirstmateTelegram.Telegram;

public sealed record TelegramRetryOptions
{
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ConflictBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TokenRejectedRecheck { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long past the long-poll timeout a <c>getUpdates</c> call may take before it counts as a timeout.</summary>
    public TimeSpan PollGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Attempts for best-effort calls, such as reactions and command answers.</summary>
    public int BestEffortAttempts { get; init; } = 3;
}

/// <summary>
/// Every Telegram call: plain text with link previews off, replies threaded to the user's message, reactions, and
/// retry with backoff (network errors and 5xx back off from 1 s to 60 s with jitter, 429 waits Telegram's
/// <c>retry_after</c>, 409 logs once and backs off 30 s, 401 rechecks every 15 minutes). The deny list is applied
/// here, in one place, to every text the bridge sends (spec 6).
/// </summary>
public sealed class TelegramGateway
{
    readonly ITelegramBotClient _client;
    readonly TimeProvider _time;
    readonly ILogger<TelegramGateway> _logger;
    readonly TelegramRetryOptions _options;
    readonly Redactor _redactor;
    readonly Func<double> _random;
    int _tokenRejected;
    int _conflict;

    public TelegramGateway(ITelegramBotClient client, TimeProvider time, ILogger<TelegramGateway> logger, TelegramRetryOptions? options = null, Func<double>? random = null, Redactor? redactor = null)
    {
        _client = client;
        _time = time;
        _logger = logger;
        _options = options ?? new TelegramRetryOptions();
        _redactor = redactor ?? Redactor.None;
        _random = random ?? Random.Shared.NextDouble;
    }

    public async Task<User> GetMeAsync(CancellationToken cancellationToken) =>
        (await CallAsync("getMe", token => _client.GetMe(token), _options.CallTimeout, bestEffort: false, cancellationToken))!;

    /// <summary>Long-polls for messages. Passing <paramref name="offset"/> confirms every update below it to Telegram.</summary>
    public async Task<Update[]> GetUpdatesAsync(int offset, int timeoutSeconds, CancellationToken cancellationToken) =>
        (await CallAsync(
            "getUpdates",
            token => _client.GetUpdates(offset, limit: 100, timeout: timeoutSeconds, allowedUpdates: [UpdateType.Message], cancellationToken: token),
            TimeSpan.FromSeconds(timeoutSeconds) + _options.PollGrace,
            bestEffort: false,
            cancellationToken))!;

    /// <summary>Sends plain text, retrying until Telegram accepts it. Returns the sent message's id. Alerts pass <paramref name="silent"/> while a mute is on; a mute never drops them.</summary>
    public async Task<int> SendTextAsync(long chatId, string text, int? replyToMessageId, bool silent = false, CancellationToken cancellationToken = default)
    {
        var sent = await CallAsync("sendMessage", token => Send(chatId, text, replyToMessageId, silent, token), _options.CallTimeout, bestEffort: false, cancellationToken);
        return sent!.Id;
    }

    /// <summary>Sends plain text with a few attempts; a failure is logged and skipped.</summary>
    public async Task<bool> TrySendTextAsync(long chatId, string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        var sent = await CallAsync("sendMessage", token => Send(chatId, text, replyToMessageId, silent: false, token), _options.CallTimeout, bestEffort: true, cancellationToken);
        return sent is not null;
    }

    /// <summary>Registers the bridge's command list for one chat only, so Telegram's command menu shows it there and nowhere else. Best-effort.</summary>
    public async Task<bool> TrySetMyCommandsAsync(long chatId, IReadOnlyList<(string Command, string Description)> commands, CancellationToken cancellationToken)
    {
        var done = await CallAsync(
            "setMyCommands",
            async token =>
            {
                await _client.SetMyCommands(
                    commands.Select(command => new BotCommand { Command = command.Command, Description = command.Description }),
                    scope: new BotCommandScopeChat { ChatId = chatId },
                    cancellationToken: token);
                return true;
            },
            _options.CallTimeout,
            bestEffort: true,
            cancellationToken);
        return done;
    }

    /// <summary>Replaces the bot's reaction on a message with <paramref name="emoji"/>; a failure is logged and skipped.</summary>
    public async Task<bool> TrySetReactionAsync(long chatId, int messageId, string emoji, CancellationToken cancellationToken)
    {
        var done = await CallAsync(
            "setMessageReaction",
            async token =>
            {
                await _client.SetMessageReaction(chatId, messageId, [new ReactionTypeEmoji { Emoji = emoji }], cancellationToken: token);
                return true;
            },
            _options.CallTimeout,
            bestEffort: true,
            cancellationToken);
        return done;
    }

    Task<Message> Send(long chatId, string text, int? replyToMessageId, bool silent, CancellationToken cancellationToken) =>
        _client.SendMessage(
            chatId,
            _redactor.Apply(text),
            replyParameters: replyToMessageId is { } messageId ? new ReplyParameters { MessageId = messageId, AllowSendingWithoutReply = true } : null,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            disableNotification: silent,
            cancellationToken: cancellationToken);

    async Task<T?> CallAsync<T>(string method, Func<CancellationToken, Task<T>> call, TimeSpan timeout, bool bestEffort, CancellationToken cancellationToken)
    {
        for (var failures = 1; ; failures++)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);
            TimeSpan wait;
            try
            {
                var result = await call(attempt.Token);
                Recovered(method);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTelegramFailure(exception))
            {
                wait = Classify(method, exception, failures, bestEffort);
            }

            if (bestEffort && (failures >= _options.BestEffortAttempts || wait == TimeSpan.MaxValue))
            {
                _logger.LogWarning("telegram {Method} skipped after {Failures} failed attempt(s)", method, failures);
                return default;
            }

            await Task.Delay(wait, _time, cancellationToken);
        }
    }

    /// <summary>Logs the failure and returns how long to wait; <see cref="TimeSpan.MaxValue"/> means a best-effort call should give up now.</summary>
    TimeSpan Classify(string method, Exception exception, int failures, bool bestEffort)
    {
        switch (exception)
        {
            case ApiRequestException { ErrorCode: 429 } tooMany:
                var retryAfter = TimeSpan.FromSeconds(Math.Max(1, tooMany.Parameters?.RetryAfter ?? 1));
                _logger.LogWarning("telegram {Method}: too many requests; waiting {Seconds} s", method, Seconds(retryAfter));
                return retryAfter;

            case ApiRequestException { ErrorCode: 401 }:
                if (Interlocked.Exchange(ref _tokenRejected, 1) == 0)
                    _logger.LogError("telegram {Method}: token rejected; checking again every {Minutes} minutes until setup saves a new token", method, _options.TokenRejectedRecheck.TotalMinutes);
                return bestEffort ? TimeSpan.MaxValue : _options.TokenRejectedRecheck;

            case ApiRequestException { ErrorCode: 409 }:
                if (Interlocked.Exchange(ref _conflict, 1) == 0)
                    _logger.LogError("telegram {Method}: conflict; another poller or a webhook is using this bot token", method);
                return bestEffort ? TimeSpan.MaxValue : _options.ConflictBackoff;

            default:
                var delay = Backoff.Jittered(Backoff.Exponential(failures, _options.InitialBackoff, _options.MaxBackoff), _random());
                _logger.LogWarning("telegram {Method} failed ({Failure}); retrying in {Seconds} s", method, Describe(exception), Seconds(delay));
                return delay;
        }
    }

    void Recovered(string method)
    {
        if (Interlocked.Exchange(ref _tokenRejected, 0) == 1)
            _logger.LogInformation("telegram {Method}: token accepted again", method);
        if (Interlocked.Exchange(ref _conflict, 0) == 1)
            _logger.LogInformation("telegram {Method}: conflict cleared", method);
    }

    static bool IsTelegramFailure(Exception exception) =>
        exception is RequestException or HttpRequestException or IOException or OperationCanceledException or TimeoutException;

    static string Describe(Exception exception) => exception switch
    {
        ApiRequestException api => $"error {api.ErrorCode.ToString(CultureInfo.InvariantCulture)}",
        RequestException { HttpStatusCode: { } status } => $"HTTP {((int)status).ToString(CultureInfo.InvariantCulture)}",
        OperationCanceledException or TimeoutException => "timed out",
        _ => "network error",
    };

    static string Seconds(TimeSpan delay) => delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
