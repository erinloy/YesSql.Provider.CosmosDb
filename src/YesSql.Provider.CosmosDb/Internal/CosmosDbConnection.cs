using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbConnection"/> shim over a Cosmos DB container. YesSql obtains this from the
/// <see cref="CosmosDbConnectionFactory"/> and drives it with the constrained SQL emitted by
/// <see cref="CosmosDbDialect"/>; <see cref="CosmosDbCommand"/> translates that SQL into Cosmos
/// SDK operations against <see cref="Container"/>.
/// </summary>
internal sealed class CosmosDbConnection : DbConnection
{
    // A CosmosClient owns its own connection pool and background threads, and the SDK expects one instance per
    // account for the life of the application. YesSql opens a DbConnection per unit of work, so a client per
    // connection exhausts sockets under load. One client is shared per (endpoint, key) for the process lifetime and
    // is never disposed by a connection. The CosmosClientOptions of the first connection opened for an account are
    // the ones that apply. Lazy<T> constructs the client exactly once under concurrent first opens.
    private static readonly ConcurrentDictionary<string, Lazy<CosmosClient>> SharedClients = new();

    // Creating the database and container are control-plane operations, which are rate limited on a real account.
    // They run once per (endpoint, database, container) for the process, not on every connection open. Lazy<Task>
    // makes concurrent first opens wait on the same operation.
    private static readonly ConcurrentDictionary<string, Lazy<Task>> EnsuredContainers = new();

    private readonly CosmosDbOptions _options;
    private CosmosClient? _client;
    private Container? _container;
    private ConnectionState _state = ConnectionState.Closed;

    public CosmosDbConnection(CosmosDbOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The Cosmos container backing the YesSql store. Available once the connection is open.</summary>
    internal Container CosmosContainer => _container
        ?? throw new InvalidOperationException("Connection is not open.");

    internal CosmosDbOptions Options => _options;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => _options.DatabaseId;
    public override string DataSource => _options.AccountEndpoint;
    public override string ServerVersion => string.Empty;
    public override ConnectionState State => _state;

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }

        _client = SharedClients.GetOrAdd(
            _options.AccountEndpoint + "\n" + _options.AccountKey,
            _ => new Lazy<CosmosClient>(() => _options.ClientOptions is null
                ? new CosmosClient(_options.AccountEndpoint, _options.AccountKey)
                : new CosmosClient(_options.AccountEndpoint, _options.AccountKey, _options.ClientOptions))).Value;

        if (_options.CreateIfNotExists)
        {
            var client = _client;
            var options = _options;
            var ensureKey = $"{options.AccountEndpoint}\n{options.DatabaseId}\n{options.ContainerId}";
            await EnsuredContainers.GetOrAdd(
                ensureKey,
                _ => new Lazy<Task>(() => EnsureDatabaseAndContainerAsync(client, options))).Value;
        }

        _container = _client.GetContainer(_options.DatabaseId, _options.ContainerId);
        _state = ConnectionState.Open;
    }

    // Runs the (rate-limited, control-plane) database/container creation a single time per process. Not bound to
    // any caller's CancellationToken on purpose: the result is shared by all connections, so a per-request cancel
    // must not poison the shared ensure. Provisioning a database/container is a one-off bootstrap, not a hot path.
    private static async Task EnsureDatabaseAndContainerAsync(CosmosClient client, CosmosDbOptions options)
    {
        var db = await client.CreateDatabaseIfNotExistsAsync(options.DatabaseId);
        await db.Database.CreateContainerIfNotExistsAsync(options.ContainerId, options.PartitionKeyPath);
    }

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override void Close() => _state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName)
        => throw new NotSupportedException("Switching databases on an open connection is not supported.");

    protected override DbCommand CreateDbCommand() => new CosmosDbCommand(this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new CosmosDbTransaction(this, isolationLevel);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Do NOT dispose the shared CosmosClient — it is a process-lifetime singleton shared by every
            // connection. Just detach this connection's references.
            _client = null;
            _container = null;
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);
    }
}
