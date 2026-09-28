using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.State;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public async Task State_and_the_request_map_survive_a_restart_in_private_files()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        using (var store = StateStore.Open(paths))
        {
            await store.UpdateStateAsync(state => state with { BotId = 5, UpdateOffset = 12, HandledUpdateIds = [14], ReplyCursor = "000000000003" });
            await store.UpdateRequestsAsync(requests => requests.SetItem("n1", new RequestEntry
            {
                NoteId = "n1",
                RequestId = "tg:5:42:9",
                ChatId = 42,
                MessageId = 9,
                SavedAt = DateTimeOffset.Parse("2026-09-26T14:05:00Z", System.Globalization.CultureInfo.InvariantCulture),
                Announced = true,
                ReplyPartsSent = 1,
            }));
        }

        using var reopened = StateStore.Open(paths);

        Assert.Equal((5L, 12, "000000000003", false), (reopened.State.BotId!.Value, reopened.State.UpdateOffset, reopened.State.ReplyCursor, reopened.State.Stopped));
        Assert.Equal([14], reopened.State.HandledUpdateIds);
        var entry = Assert.Single(reopened.Requests.Values);
        Assert.Equal(("tg:5:42:9", 42L, 9, 1, true), (entry.RequestId, entry.ChatId, entry.MessageId, entry.ReplyPartsSent, entry.Announced));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.StateFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.RequestsFile));
        Assert.Empty(Directory.GetFiles(paths.StateDirectory, "*.tmp"));
    }

    [Fact]
    public async Task A_failed_write_leaves_the_previous_file_and_the_memory_state_untouched()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        var writer = new CrashingStateWriter();
        using var store = StateStore.Open(paths, writer);
        await store.UpdateStateAsync(state => state with { UpdateOffset = 3 });
        writer.CrashOn("state.json");

        await Assert.ThrowsAsync<SimulatedCrashException>(() => store.UpdateStateAsync(state => state with { UpdateOffset = 4 }));

        Assert.Equal(3, store.State.UpdateOffset);
        using var reopened = StateStore.Open(paths);
        Assert.Equal(3, reopened.State.UpdateOffset);
    }

    [Theory]
    [InlineData("""{"schema":"firstmate-telegram.state.v1","update_offset":7}""")]
    [InlineData("""{"schema":"firstmate-telegram.state.v1","update_offset":7,"handled_update_ids":null,"reply_cursor":null,"settles":null}""")]
    public void A_state_file_missing_later_members_opens_with_their_defaults(string contents)
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(paths.StateFile, contents);

        using var store = StateStore.Open(paths);

        Assert.Equal(7, store.State.UpdateOffset);
        Assert.Equal((0, "", 0), (store.State.HandledUpdateIds.Count, store.State.ReplyCursor, store.State.Settles.Count));
    }

    [Fact]
    public void An_unreadable_state_file_is_refused_with_a_way_out()
    {
        using var directory = new TempDirectory();
        var paths = Paths(directory);
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(paths.StateFile, "{ torn");

        var error = Assert.Throws<BridgeException>(() => StateStore.Open(paths));

        Assert.Contains("Move it aside", error.Message);
    }

    [Fact]
    public void Only_one_process_holds_the_instance_lock()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("state", "lock");

        using (var first = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(path));
        }

        using var again = InstanceLock.TryAcquire(path);
        Assert.NotNull(again);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    static BridgePaths Paths(TempDirectory directory) => BridgePaths.FromEnvironment(name => name == "HOME" ? directory.Path : null);
}
