using System.Diagnostics;
using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Infrastructure;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>Runs only when <c>FIRSTMATE_CONTRACT_ROOT</c> names a FirstMate checkout.</summary>
public sealed class ContractFactAttribute : FactAttribute
{
    public const string Variable = "FIRSTMATE_CONTRACT_ROOT";

    public ContractFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"set {Variable} to a FirstMate checkout to run the contract tests";
    }
}

/// <summary>
/// The FirstMate contract against FirstMate's real scripts, in a throwaway home whose <c>bin/</c> links to the
/// checkout. They catch schema drift when FirstMate changes. They never touch a real FirstMate home.
/// </summary>
public sealed class ContractTests
{
    [ContractFact]
    public async Task Note_replay_receipts_and_ready_match_the_contract()
    {
        using var home = ThrowawayHome();
        var client = new FirstMateClient(new SystemProcessRunner(), home.Path);

        var created = await client.SaveNoteAsync("tg:1:2:3", "contract request\n\n-- footer --\n", CancellationToken.None);
        var replayed = await client.SaveNoteAsync("tg:1:2:3", "a different body", CancellationToken.None);

        Assert.True(created.IsSaved, created.Failure?.Reason);
        Assert.Equal(("created", "replay", created.NoteId), (created.Outcome, replayed.Outcome, replayed.NoteId));
        await RunInboxAsync(home, ["reply", created.NoteId!, "-"], "contract reply");

        var receipts = (await client.ReadReceiptsAsync("", CancellationToken.None)).Receipts!;
        var reply = Assert.Single(receipts.Replies);
        Assert.Equal((created.NoteId, "contract reply"), (reply.NoteId, reply.Body));
        Assert.Empty((await client.ReadReceiptsAsync(receipts.ReplyCursor, CancellationToken.None)).Receipts!.Replies);

        Assert.True((await client.AnnounceAsync(created.NoteId!, CancellationToken.None)).Repaired);
        var ready = await client.ReadReadyAsync(CancellationToken.None);
        Assert.Null(ready.Failure);
        Assert.Equal(Readiness.NotRunning, ReadinessRules.Classify(ready));
    }

    static TempDirectory ThrowawayHome()
    {
        var root = Environment.GetEnvironmentVariable(ContractFactAttribute.Variable)!;
        var home = new TempDirectory();
        Directory.CreateDirectory(home.Combine("state"));
        Directory.CreateDirectory(home.Combine("data"));
        File.CreateSymbolicLink(home.Combine("bin"), Path.Combine(root, "bin"));
        return home;
    }

    static async Task RunInboxAsync(TempDirectory home, string[] arguments, string input)
    {
        var start = new ProcessStartInfo(home.Combine("bin", "fm-inbox.sh"), arguments) { RedirectStandardInput = true };
        start.Environment["FM_HOME"] = home.Path;
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
