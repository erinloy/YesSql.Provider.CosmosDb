using System;
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
public sealed class CosmosDbConnection : DbConnection
{
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

        _client ??= _options.ClientOptions is null
            ? new CosmosClient(_options.AccountEndpoint, _options.AccountKey)
            : new CosmosClient(_options.AccountEndpoint, _options.AccountKey, _options.ClientOptions);

        if (_options.CreateIfNotExists)
        {
            var db = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseId, cancellationToken: cancellationToken);
            await db.Database.CreateContainerIfNotExistsAsync(_options.ContainerId, _options.PartitionKeyPath, cancellationToken: cancellationToken);
        }

        _container = _client.GetContainer(_options.DatabaseId, _options.ContainerId);
        _state = ConnectionState.Open;
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
            _client?.Dispose();
            _client = null;
            _container = null;
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);
    }
}
