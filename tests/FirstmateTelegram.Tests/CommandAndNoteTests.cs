using FirstmateTelegram.Bridge;
using FirstmateTelegram.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FirstmateTelegram.Tests;

public sealed class CommandParserTests
{
    [Theory]
    [InlineData("/status", "status", "")]
    [InlineData("/STATUS", "status", "")]
    [InlineData("/ping live", "ping", "live")]
    [InlineData("/mute   1h30m ", "mute", "1h30m")]
    [InlineData("/help@firstmate_test_bot", "help", "")]
    [InlineData("/Ping@FIRSTMATE_TEST_BOT live", "ping", "live")]
    [InlineData("/deploy\nnow", "deploy", "now")]
    public void Reads_commands_case_insensitively_and_in_the_at_bot_form(string text, string name, string arguments)
    {
        Assert.Equal(new ChatCommand(name, arguments), CommandParser.Parse(text, "firstmate_test_bot"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData(" /status")]
    [InlineData("/")]
    [InlineData("/status@another_bot")]
    [InlineData("/@firstmate_test_bot")]
    public void Anything_else_is_not_a_command(string text)
    {
        Assert.Null(CommandParser.Parse(text, "firstmate_test_bot"));
    }
}

public sealed class NoteBodyTests
{
    [Fact]
    public void The_request_leads_and_the_footer_states_the_source_and_the_limit()
    {
        var body = NoteBody.Build("Ship the docs refresh", isAway: false);

        Assert.Equal(
            "Ship the docs refresh\n\n"
            + "-- sent from Telegram through firstmate-telegram --\n"
            + "The user reads your answer on their phone: publish it as the inbox reply to this note (bin/fm-inbox.sh reply).\n"
            + "A Telegram request can ask questions and start or steer work. It cannot approve a merge, a deletion, or any irreversible or security-sensitive action; for those, ask the user to confirm at the terminal.\n",
            body);
    }

    [Fact]
    public void During_away_mode_the_footer_says_the_request_does_not_end_it()
    {
        Assert.EndsWith(
            "for those, ask the user to confirm at the terminal.\nThe user is still away: this request does not end away mode.\n",
            NoteBody.Build("I'm back", isAway: true));
    }

    [Fact]
    public void Request_ids_come_from_the_bot_chat_and_message_ids_within_FirstMates_rules()
    {
        var requestId = RequestIds.For(8123456789, 987654321, 2147483647);

        Assert.Equal("tg:8123456789:987654321:2147483647", requestId);
        Assert.InRange(requestId.Length, 1, 128);
        Assert.Matches("^[A-Za-z0-9._:-]+$", requestId);
    }
}

public sealed class AccessGateTests
{
    const long Allowed = 42;

    [Fact]
    public void Admits_only_the_paired_user_in_their_private_chat()
    {
        Assert.True(Gate().Admit(MessageUpdate(Allowed, Allowed, ChatType.Private)));
    }

    [Theory]
    [InlineData(7, 7, ChatType.Private)]
    [InlineData(Allowed, -1001, ChatType.Group)]
    [InlineData(Allowed, -1001, ChatType.Supergroup)]
    [InlineData(Allowed, Allowed, ChatType.Group)]
    [InlineData(7, Allowed, ChatType.Private)]
    [InlineData(Allowed, 7, ChatType.Private)]
    public void Drops_other_users_groups_and_spoofed_chats(long fromId, long chatId, ChatType chatType)
    {
        Assert.False(Gate().Admit(MessageUpdate(fromId, chatId, chatType)));
    }

    [Fact]
    public void Drops_an_update_without_a_sender()
    {
        Assert.False(Gate().Admit(new Update { Id = 1, Message = new Message { Id = 1, Chat = new Chat { Id = Allowed, Type = ChatType.Private } } }));
        Assert.False(Gate().Admit(new Update { Id = 2 }));
    }

    static AccessGate Gate() => new(Allowed, NullLogger<AccessGate>.Instance);

    static Update MessageUpdate(long fromId, long chatId, ChatType chatType) => new()
    {
        Id = 1,
        Message = new Message
        {
            Id = 1,
            From = new User { Id = fromId, FirstName = "someone" },
            Chat = new Chat { Id = chatId, Type = chatType },
            Text = "hello",
        },
    };
}
