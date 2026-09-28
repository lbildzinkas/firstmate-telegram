using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FirstmateTelegram.Configuration;
using FirstmateTelegram.Infrastructure;

namespace FirstmateTelegram.State;

/// <summary>Writes one state file durably. The default is an atomic write; tests inject failures here.</summary>
public interface IStateWriter
{
    void Write(string path, string contents);
}

public sealed class AtomicStateWriter : IStateWriter
{
    public void Write(string path, string contents) => PrivateFiles.WriteAtomic(path, contents);
}

/// <summary>
/// The bridge's own state (<c>state.json</c>), request map (<c>requests.json</c>) and alert history (<c>alerts.json</c>),
/// held in memory and written through on every change, with one writer at a time.
/// </summary>
public sealed class StateStore : IDisposable
{
    readonly BridgePaths _paths;
    readonly IStateWriter _writer;
    readonly SemaphoreSlim _gate = new(1, 1);

    StateStore(BridgePaths paths, IStateWriter writer, BridgeState state, ImmutableDictionary<string, RequestEntry> requests, ImmutableDictionary<string, AlertEntry> alerts)
    {
        _paths = paths;
        _writer = writer;
        State = state;
        Requests = requests;
        Alerts = alerts;
    }

    public BridgeState State { get; private set; }

    /// <summary>The request map, keyed by FirstMate note id.</summary>
    public ImmutableDictionary<string, RequestEntry> Requests { get; private set; }

    /// <summary>The alert history, keyed by dedupe key.</summary>
    public ImmutableDictionary<string, AlertEntry> Alerts { get; private set; }

    public static StateStore Open(BridgePaths paths, IStateWriter? writer = null)
    {
        var state = WithDefaults(Read(paths.StateFile, StateJsonContext.Default.BridgeState, BridgeState.SchemaName, document => document.Schema) ?? new BridgeState());
        var requests = Read(paths.RequestsFile, StateJsonContext.Default.RequestsDocument, RequestsDocument.SchemaName, document => document.Schema) ?? new RequestsDocument();
        var alerts = Read(paths.AlertsFile, StateJsonContext.Default.AlertsDocument, AlertsDocument.SchemaName, document => document.Schema) ?? new AlertsDocument();
        var requestMap = requests.Requests.ToImmutableDictionary(entry => entry.NoteId, StringComparer.Ordinal);
        var alertMap = alerts.Alerts.ToImmutableDictionary(entry => entry.Key, StringComparer.Ordinal);
        return new StateStore(paths, writer ?? new AtomicStateWriter(), state, requestMap, alertMap);
    }

    public async Task UpdateStateAsync(Func<BridgeState, BridgeState> change, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = change(State);
            if (next == State)
                return;

            Persist(_paths.StateFile, next, StateJsonContext.Default.BridgeState);
            State = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateRequestsAsync(
        Func<ImmutableDictionary<string, RequestEntry>, ImmutableDictionary<string, RequestEntry>> change,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = change(Requests);
            if (ReferenceEquals(next, Requests))
                return;

            var document = new RequestsDocument { Requests = [.. next.Values.OrderBy(entry => entry.SavedAt).ThenBy(entry => entry.NoteId, StringComparer.Ordinal)] };
            Persist(_paths.RequestsFile, document, StateJsonContext.Default.RequestsDocument);
            Requests = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAlertsAsync(
        Func<ImmutableDictionary<string, AlertEntry>, ImmutableDictionary<string, AlertEntry>> change,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = change(Alerts);
            if (ReferenceEquals(next, Alerts))
                return;

            var document = new AlertsDocument { Alerts = [.. next.Values.OrderBy(entry => entry.At).ThenBy(entry => entry.Key, StringComparer.Ordinal)] };
            Persist(_paths.AlertsFile, document, StateJsonContext.Default.AlertsDocument);
            Alerts = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    void Persist<T>(string path, T document, JsonTypeInfo<T> typeInfo)
    {
        PrivateFiles.EnsureDirectory(_paths.StateDirectory);
        _writer.Write(path, JsonSerializer.Serialize(document, typeInfo) + "\n");
    }

    /// <summary>
    /// The generated reader sets a member missing from the file to null instead of keeping its initializer. A state
    /// file from before the settle watches has no settles member, and a bridge that read it wrote it back as null;
    /// both load as empty.
    /// </summary>
    static BridgeState WithDefaults(BridgeState state) => state with
    {
        HandledUpdateIds = state.HandledUpdateIds ?? [],
        ReplyCursor = state.ReplyCursor ?? "",
        Settles = state.Settles ?? [],
    };

    static T? Read<T>(string path, JsonTypeInfo<T> typeInfo, string schema, Func<T, string> schemaOf)
        where T : class
    {
        if (!File.Exists(path))
            return null;

        T? document;
        try
        {
            document = JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch (JsonException exception)
        {
            throw new BridgeException($"The bridge state file {path} cannot be read ({exception.Message}). Move it aside to start from a fresh state.", exception);
        }

        if (document is null || schemaOf(document) != schema)
            throw new BridgeException($"The bridge state file {path} is not a {schema} file. Move it aside to start from a fresh state.");
        return document;
    }

    public void Dispose() => _gate.Dispose();
}
