using System.Diagnostics;
using System.Globalization;
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

    [ContractFact]
    public async Task Bearings_and_fleet_snapshots_match_the_contract()
    {
        using var home = SeededHome();
        var client = new FirstMateClient(new SystemProcessRunner(), home.Path);

        var bearings = await client.ReadBearingsAsync(CancellationToken.None);

        Assert.Null(bearings.Failure);
        Assert.Equal(["Billing API versioning: Options: A path prefix, B header"], bearings.Snapshot!.OpenDecisions);
        Assert.Contains(bearings.Snapshot.InFlight, work => work is { Id: "ship-task", Name: "Fix the login redirect" });
        Assert.Equal("ship-task", Assert.Single(bearings.Snapshot.Gates).BlockedBy);
        Assert.Equal("Docs refresh", Assert.Single(bearings.Snapshot.Landed).What);

        var fleet = await client.ReadFleetSnapshotAsync(CancellationToken.None);

        Assert.Null(fleet.Failure);
        var decision = Assert.Single(fleet.Snapshot!.Records, record => record.CaptainActionable);
        Assert.Equal(("billing-choice", "Billing API versioning", "Options: A path prefix, B header"), (decision.Id, decision.Title, decision.HoldReason));
        Assert.Equal(["ship-task"], Assert.Single(fleet.Snapshot.Records, record => record.Id == "release-notes").UnresolvedBlockerIds);
        Assert.NotNull(Assert.Single(fleet.Snapshot.Records, record => record.State == "done").CompletedAt);
        Assert.Contains(fleet.Snapshot.Tasks, task => task is { Title: "Fix the login redirect", SecondMate: false });
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

    /// <summary>A home with one held decision, one in-flight task, one queued task and one landed item, so the snapshot scripts have real rows to project.</summary>
    static TempDirectory SeededHome()
    {
        var home = ThrowawayHome();
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(home.Combine("data", "backlog.md"), $"""
            ## In flight
            - [ ] ship-task - Fix the login redirect (repo: acme/webapp) (kind: ship) (since {yesterday})

            ## Queued
            - [ ] release-notes - Release notes blocked-by: ship-task (repo: acme/webapp) (kind: ship)
            - [ ] billing-choice - Billing API versioning (repo: acme/webapp) (kind: captain) (hold: Options: A path prefix, B header) (hold-kind: captain) (since {yesterday})

            ## Done
            - [x] docs-refresh - Docs refresh https://github.com/acme/webapp/pull/5 (repo: acme/webapp) (kind: ship) (merged {yesterday})
            """);
        File.WriteAllText(home.Combine("state", "ship-task.meta"), $"""
            window=firstmate:fm-ship-task
            worktree={home.Path}/projects/ship-wt
            project=acme/webapp
            harness=claude
            kind=ship
            """);
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
