using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FirstmateTelegram.Tests.Fakes;

/// <summary>
/// An in-process Bot API server on a loopback port. It implements the methods the bridge uses, serves scripted
/// updates with Telegram's offset semantics (an offset confirms, and deletes, every update below it), records
/// every call, and fails calls on request. Nothing here reaches the real Telegram API.
/// </summary>
public sealed class FakeTelegramServer : IAsyncDisposable
{
    public const string Token = "123456789:AAHfakeTokenForTestsOnly-0123456789ab";
    public const long BotId = 123456789;
    public const string BotUsername = "firstmate_test_bot";

    readonly WebApplication _app;
    readonly Lock _gate = new();
    readonly List<JsonObject> _updates = [];
    readonly Dictionary<string, Queue<(int Code, string Description, int? RetryAfter)>> _failures = [];
    readonly List<(string Method, JsonObject Body)> _calls = [];
    SemaphoreSlim _arrived = new(0);
    int _nextUpdateId = 500;
    int _nextUserMessageId = 1;
    int _nextBotMessageId = 9000;

    FakeTelegramServer(WebApplication app) => _app = app;

    public string BaseUrl => _app.Urls.First();

    /// <summary>The token this server accepts; anything else gets 401, as a revoked token does.</summary>
    public string AcceptedToken { get; set; } = Token;

    public string? WebhookUrl { get; set; }

    /// <summary>How long an empty long poll waits, however long the caller asked for.</summary>
    public TimeSpan MaxLongPoll { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The highest offset any <c>getUpdates</c> call has passed, which is Telegram's confirmation.</summary>
    public int ConfirmedOffset { get; private set; }

    public static async Task<FakeTelegramServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeTelegramServer(app);
        app.MapPost("/bot{token}/{method}", server.HandleAsync);
        await app.StartAsync();
        return server;
    }

    public int EnqueueText(long fromId, string text, long? chatId = null, string chatType = "private") =>
        Enqueue(fromId, chatId ?? fromId, chatType, message => message["text"] = text);

    public int EnqueuePhoto(long fromId) =>
        Enqueue(fromId, fromId, "private", message => message["photo"] = new JsonArray(new JsonObject
        {
            ["file_id"] = "photo-file",
            ["file_unique_id"] = "photo-unique",
            ["width"] = 10,
            ["height"] = 10,
        }));

    public void FailNext(string method, int code, string description, int? retryAfter = null, int times = 1)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(method, out var queue))
                _failures[method] = queue = new();
            for (var index = 0; index < times; index++)
                queue.Enqueue((code, description, retryAfter));
        }
    }

    public sealed record SentMessage(long ChatId, string Text, int? ReplyToMessageId, bool? AllowSendingWithoutReply, bool LinkPreviewDisabled, string? ParseMode);

    public sealed record Reaction(long ChatId, int MessageId, IReadOnlyList<string> Emoji);

    public IReadOnlyList<SentMessage> SentMessages()
    {
        lock (_gate)
        {
            return _calls.Where(call => call.Method == "sendMessage").Select(call => new SentMessage(
                call.Body["chat_id"]!.GetValue<long>(),
                call.Body["text"]!.GetValue<string>(),
                call.Body["reply_parameters"]?["message_id"]?.GetValue<int>(),
                call.Body["reply_parameters"]?["allow_sending_without_reply"]?.GetValue<bool>(),
                call.Body["link_preview_options"]?["is_disabled"]?.GetValue<bool>() == true,
                call.Body["parse_mode"]?.GetValue<string>())).ToList();
        }
    }

    public IReadOnlyList<Reaction> Reactions()
    {
        lock (_gate)
        {
            return _calls.Where(call => call.Method == "setMessageReaction").Select(call => new Reaction(
                call.Body["chat_id"]!.GetValue<long>(),
                call.Body["message_id"]!.GetValue<int>(),
                call.Body["reaction"]!.AsArray().Select(reaction => reaction!["emoji"]!.GetValue<string>()).ToList())).ToList();
        }
    }

    public IReadOnlyList<string> Methods()
    {
        lock (_gate)
            return _calls.Select(call => call.Method).ToList();
    }

    public IReadOnlyList<JsonObject> CallsTo(string method)
    {
        lock (_gate)
            return _calls.Where(call => call.Method == method).Select(call => call.Body).ToList();
    }

    public int PendingUpdateCount()
    {
        lock (_gate)
            return _updates.Count;
    }

    int Enqueue(long fromId, long chatId, string chatType, Action<JsonObject> fill)
    {
        lock (_gate)
        {
            var messageId = _nextUserMessageId++;
            var message = new JsonObject
            {
                ["message_id"] = messageId,
                ["from"] = new JsonObject { ["id"] = fromId, ["is_bot"] = false, ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["username"] = "ada" },
                ["chat"] = chatType == "private"
                    ? new JsonObject { ["id"] = chatId, ["type"] = chatType, ["first_name"] = "Ada" }
                    : new JsonObject { ["id"] = chatId, ["type"] = chatType, ["title"] = "A group" },
                ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            fill(message);
            _updates.Add(new JsonObject { ["update_id"] = _nextUpdateId++, ["message"] = message });
            _arrived.Release();
            return messageId;
        }
    }

    async Task HandleAsync(HttpContext context, string token, string method)
    {
        var body = await ReadBodyAsync(context.Request);
        (int Code, string Description, int? RetryAfter)? failure = null;
        lock (_gate)
        {
            _calls.Add((method, body));
            if (_failures.TryGetValue(method, out var queue) && queue.Count > 0)
                failure = queue.Dequeue();
        }

        if (token != AcceptedToken)
            failure = (401, "Unauthorized", null);
        if (failure is { } error)
        {
            await RespondErrorAsync(context.Response, error.Code, error.Description, error.RetryAfter);
            return;
        }

        JsonNode? result = method switch
        {
            "getMe" => new JsonObject { ["id"] = BotId, ["is_bot"] = true, ["first_name"] = "FirstMate", ["username"] = BotUsername },
            "getUpdates" => await GetUpdatesAsync(body, context.RequestAborted),
            "sendMessage" => SendMessage(body),
            "setMessageReaction" => JsonValue.Create(true),
            "getWebhookInfo" => new JsonObject { ["url"] = WebhookUrl ?? "", ["has_custom_certificate"] = false, ["pending_update_count"] = PendingUpdateCount() },
            "deleteWebhook" => DeleteWebhook(),
            _ => null,
        };
        if (result is null)
        {
            await RespondErrorAsync(context.Response, 404, "Not Found: method not found", null);
            return;
        }

        await RespondAsync(context.Response, 200, new JsonObject { ["ok"] = true, ["result"] = result });
    }

    async Task<JsonNode> GetUpdatesAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var offset = body["offset"]?.GetValue<int>();
        var timeout = body["timeout"]?.GetValue<int>() ?? 0;
        var limit = body["limit"]?.GetValue<int>() ?? 100;
        var deadline = DateTimeOffset.UtcNow + (timeout > 0 ? MaxLongPoll : TimeSpan.Zero);
        while (true)
        {
            SemaphoreSlim arrived;
            lock (_gate)
            {
                if (offset is { } confirmed)
                {
                    _updates.RemoveAll(update => update["update_id"]!.GetValue<int>() < confirmed);
                    ConfirmedOffset = Math.Max(ConfirmedOffset, confirmed);
                }

                var available = _updates.Where(update => offset is null || update["update_id"]!.GetValue<int>() >= offset).Take(limit).ToList();
                if (available.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                    return new JsonArray(available.Select(update => (JsonNode)update.DeepClone()).ToArray());
                arrived = _arrived = new SemaphoreSlim(0);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await arrived.WaitAsync(remaining, cancellationToken);
        }
    }

    JsonObject SendMessage(JsonObject body)
    {
        int messageId;
        lock (_gate)
            messageId = _nextBotMessageId++;
        return new JsonObject
        {
            ["message_id"] = messageId,
            ["from"] = new JsonObject { ["id"] = BotId, ["is_bot"] = true, ["first_name"] = "FirstMate", ["username"] = BotUsername },
            ["chat"] = new JsonObject { ["id"] = body["chat_id"]!.DeepClone(), ["type"] = "private" },
            ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["text"] = body["text"]!.DeepClone(),
        };
    }

    JsonValue DeleteWebhook()
    {
        WebhookUrl = null;
        return JsonValue.Create(true);
    }

    static async Task<JsonObject> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text)!.AsObject();
    }

    static Task RespondErrorAsync(HttpResponse response, int code, string description, int? retryAfter)
    {
        var error = new JsonObject { ["ok"] = false, ["error_code"] = code, ["description"] = description };
        if (retryAfter is { } seconds)
            error["parameters"] = new JsonObject { ["retry_after"] = seconds };
        return RespondAsync(response, code, error);
    }

    static async Task RespondAsync(HttpResponse response, int status, JsonObject document)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        await response.WriteAsync(document.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
