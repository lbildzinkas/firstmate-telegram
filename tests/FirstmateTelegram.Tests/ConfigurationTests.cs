using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Telegram;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class ConfigFileTests
{
    [Fact]
    public void Only_the_home_and_the_allowed_user_are_required_and_the_rest_default()
    {
        var config = ConfigFile.Parse("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/Users/me/firstmate","allowed_user_id":123456789}""");

        Assert.Equal(new BridgeConfig { FirstmateHome = "/Users/me/firstmate", AllowedUserId = 123456789 } with { DenyList = config.DenyList }, config);
        Assert.Empty(config.DenyList);
        Assert.Equal(("claude", 60, 15, 15, BotReactions.DefaultReplied), (config.QuotaProvider, config.LivePingTimeoutSeconds, config.UnresponsiveAfterMinutes, config.AlertSettleMinutes, config.RepliedReaction));
    }

    [Fact]
    public void The_documented_example_round_trips()
    {
        const string example = """
            {
              "schema": "firstmate-telegram.config.v1",
              "firstmate_home": "/Users/me/firstmate",
              "allowed_user_id": 123456789,
              "deny_list": [],
              "quota_provider": "claude",
              "live_ping_timeout_seconds": 60,
              "unresponsive_after_minutes": 15,
              "alert_settle_minutes": 15,
              "replied_reaction": "👌"
            }
            """;

        var config = ConfigFile.Parse(example);

        Assert.Equal(example.Trim(), ConfigFile.Serialize(config).Trim());
    }

    [Theory]
    [InlineData("""{"firstmate_home":"/h","allowed_user_id":1}""", "\"schema\" is required")]
    [InlineData("""{"schema":"firstmate-telegram.config.v2","firstmate_home":"/h","allowed_user_id":1}""", "\"schema\" must be")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","allowed_user_id":1}""", "\"firstmate_home\" is required")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"relative/home","allowed_user_id":1}""", "absolute path")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h"}""", "\"allowed_user_id\" is required")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":"123"}""", "whole number")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":-5}""", "positive")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"replied_reaction":"✅"}""", "\"replied_reaction\" must be one of")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"alert_settle_minutes":0}""", "positive whole number")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"alowed_user_id":1}""", "unknown key \"alowed_user_id\"")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"deny_list":"acme"}""", "list of strings")]
    [InlineData("""{"schema":"firstmate-telegram.config.v1","firstmate_home":"/h","allowed_user_id":1,"deny_list":["acme"]}""", "\"deny_list\" is not supported by this version")]
    [InlineData("""not json""", "not valid JSON")]
    public void An_invalid_file_is_refused_with_a_message_that_names_the_problem(string json, string expected)
    {
        var error = Assert.Throws<BridgeException>(() => ConfigFile.Parse(json));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Writing_creates_a_private_folder_and_file()
    {
        using var directory = new TempDirectory();
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);

        ConfigFile.Write(paths, new BridgeConfig { FirstmateHome = "/Users/me/firstmate", AllowedUserId = 7 });

        Assert.Equal(7, ConfigFile.Load(paths).AllowedUserId);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.ConfigFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.ConfigDirectory));
    }
}

public sealed class BridgePathsTests
{
    [Fact]
    public void Uses_the_documented_locations_under_home()
    {
        var paths = BridgePaths.FromEnvironment(name => name == "HOME" ? "/Users/me" : null);

        Assert.Equal("/Users/me/.config/firstmate-telegram/token", paths.TokenFile);
        Assert.Equal("/Users/me/.config/firstmate-telegram/config.json", paths.ConfigFile);
        Assert.Equal("/Users/me/.local/state/firstmate-telegram/state.json", paths.StateFile);
        Assert.Equal("/Users/me/.local/state/firstmate-telegram/requests.json", paths.RequestsFile);
        Assert.Equal("/Users/me/.local/state/firstmate-telegram/lock", paths.LockFile);
        Assert.Equal("/Users/me/Library/Logs/firstmate-telegram/bridge.log", paths.LogFile);
        Assert.Equal("/Users/me/.local/share/firstmate-telegram/app/firstmate-telegram", paths.InstalledProgram);
        Assert.Equal("/Users/me/.local/bin/firstmate-telegram", paths.BinLink);
        Assert.Equal("/Users/me/Library/LaunchAgents/io.github.lbildzinkas.firstmate-telegram.plist", paths.LaunchAgentPlist);
    }

    [Fact]
    public void Honours_absolute_XDG_folders_and_ignores_relative_ones()
    {
        var absolute = BridgePaths.FromEnvironment(name => name switch
        {
            "HOME" => "/Users/me",
            "XDG_CONFIG_HOME" => "/xdg/config",
            "XDG_STATE_HOME" => "/xdg/state",
            _ => null,
        });
        var relative = BridgePaths.FromEnvironment(name => name switch
        {
            "HOME" => "/Users/me",
            "XDG_CONFIG_HOME" => "config",
            _ => null,
        });

        Assert.Equal("/xdg/config/firstmate-telegram", absolute.ConfigDirectory);
        Assert.Equal("/xdg/state/firstmate-telegram", absolute.StateDirectory);
        Assert.Equal("/Users/me/.config/firstmate-telegram", relative.ConfigDirectory);
    }
}

public sealed class TokenFileTests
{
    [Fact]
    public void The_token_is_saved_privately_and_read_back()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);

        TokenFile.Write(paths, "  123:abc  ");

        Assert.Equal("123:abc", TokenFile.Read(paths));
        Assert.Equal("123:abc\n", File.ReadAllText(paths.TokenFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.TokenFile));
    }

    [Fact]
    public void A_token_file_readable_by_others_is_refused_with_the_fix()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        TokenFile.Write(paths, "123:abc");
        File.SetUnixFileMode(paths.TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var error = Assert.Throws<BridgeException>(() => TokenFile.Read(paths));

        Assert.Contains($"chmod 600 '{paths.TokenFile}'", error.Message);
    }

    [Fact]
    public void A_token_folder_readable_by_the_group_is_refused_with_the_fix()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        TokenFile.Write(paths, "123:abc");
        File.SetUnixFileMode(paths.ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var error = Assert.Throws<BridgeException>(() => TokenFile.Read(paths));

        Assert.Contains($"chmod 700 '{paths.ConfigDirectory}'", error.Message);
    }

    [Fact]
    public void A_missing_token_says_to_run_setup()
    {
        using var directory = new TempDirectory();

        Assert.Contains("firstmate-telegram setup", Assert.Throws<BridgeException>(() => TokenFile.Read(Paths(directory))).Message);
    }

    static BridgePaths Paths(TempDirectory directory) => BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
}
