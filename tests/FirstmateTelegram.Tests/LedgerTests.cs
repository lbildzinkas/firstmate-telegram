using FirstmateTelegram.FirstMate;
using FirstmateTelegram.Tests.Fakes;
using FirstmateTelegram.Tests.Support;

namespace FirstmateTelegram.Tests;

/// <summary>How the bridge tails FirstMate's fleet activity ledger (spec 7.2.6): offsets, file identity, partial lines and unknown versions.</summary>
public sealed class LedgerTests
{
    const string Dispatched = """{"v":1,"ts":"2026-09-26T19:00:00Z","event":"task.dispatched","task":"t1","kind":"ship","project":"acme/webapp"}""";
    const string PrReady = """{"v":1,"ts":"2026-09-26T19:01:00Z","event":"task.pr_ready","task":"t1","pr":"https://github.com/acme/webapp/pull/7"}""";
    const string Status = """{"v":1,"ts":"2026-09-26T19:02:00Z","event":"task.status","task":"t1","state":"blocked","key":"b1","text":"waiting on the build"}""";

    [Fact]
    public void A_first_read_takes_the_files_end_as_its_starting_point_and_returns_nothing()
    {
        using var home = new FakeFirstMateHome();
        home.AppendLedger(Dispatched, PrReady);
        var ledger = new FleetLedger(home.Home);

        var read = ledger.Read(from: null)!;

        Assert.Empty(read.Events);
        Assert.Equal(File.ReadAllText(home.LedgerPath).Length, read.Position.Offset);
    }

    [Fact]
    public void New_complete_lines_after_the_position_are_read_once_each()
    {
        using var home = new FakeFirstMateHome();
        var ledger = new FleetLedger(home.Home);
        home.AppendLedger(Dispatched);
        var position = ledger.Read(from: null)!.Position;

        home.AppendLedger(PrReady, Status);
        var read = ledger.Read(position)!;

        Assert.Equal(["pr:t1:https://github.com/acme/webapp/pull/7", "status:t1:blocked"], read.Events.Select(Describe));
        Assert.Equal(File.ReadAllText(home.LedgerPath).Length, read.Position.Offset);
        Assert.Empty(ledger.Read(read.Position)!.Events);
    }

    [Fact]
    public void A_partial_last_line_waits_for_the_next_read()
    {
        using var home = new FakeFirstMateHome();
        var ledger = new FleetLedger(home.Home);
        home.AppendLedger(Dispatched);
        var position = ledger.Read(from: null)!.Position;

        File.AppendAllText(home.LedgerPath, PrReady[..^20]); // no trailing newline yet
        var partial = ledger.Read(position)!;
        Assert.Empty(partial.Events);
        Assert.Equal(position.Offset, partial.Position.Offset);

        File.AppendAllText(home.LedgerPath, PrReady[^20..] + "\n");
        var read = ledger.Read(partial.Position)!;
        Assert.Equal(["pr:t1:https://github.com/acme/webapp/pull/7"], read.Events.Select(Describe));
    }

    [Fact]
    public void A_replaced_file_is_read_from_the_beginning_with_a_new_identity()
    {
        using var home = new FakeFirstMateHome();
        var ledger = new FleetLedger(home.Home);
        home.AppendLedger(Dispatched);
        var position = ledger.Read(from: null)!.Position;

        home.ReplaceLedger(PrReady);
        var read = ledger.Read(position)!;

        Assert.Equal(["pr:t1:https://github.com/acme/webapp/pull/7"], read.Events.Select(Describe));
        Assert.NotEqual((position.Device, position.Inode), (read.Position.Device, read.Position.Inode));
    }

    [Fact]
    public void Records_with_another_version_are_skipped_and_counted_and_unknown_events_are_ignored()
    {
        using var home = new FakeFirstMateHome();
        var ledger = new FleetLedger(home.Home);
        home.AppendLedger(
            """{"v":2,"ts":"2026-09-26T19:00:00Z","event":"task.status","task":"t","state":"failed"}""",
            """{"v":1,"ts":"2026-09-26T19:00:01Z","event":"task.dreamed","task":"t"}""");
        var position = ledger.Read(from: null)!.Position;

        home.AppendLedger(
            """{"v":2,"ts":"2026-09-26T19:03:00Z","event":"task.status","task":"t2","state":"failed"}""",
            Status);
        var read = ledger.Read(position)!;

        Assert.Equal(1, read.SkippedOtherVersions);
        var status = Assert.IsType<LedgerEvent.Status>(Assert.Single(read.Events));
        Assert.Equal("blocked", status.State);
        Assert.Equal("waiting on the build", status.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T19:02:00Z"), status.At);
    }

    [Fact]
    public void A_missing_ledger_file_reads_as_null()
    {
        using var home = new FakeFirstMateHome();
        File.Delete(home.LedgerPath);

        Assert.Null(new FleetLedger(home.Home).Read(from: null));
    }

    static string Describe(LedgerEvent anEvent) => anEvent switch
    {
        LedgerEvent.Dispatched dispatched => $"dispatched:{dispatched.Task}:{dispatched.Kind}:{dispatched.Project}",
        LedgerEvent.PrReady pr => $"pr:{pr.Task}:{pr.PrUrl}",
        LedgerEvent.Status status => $"status:{status.Task}:{status.State}",
        _ => "unknown",
    };
}
