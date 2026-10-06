using System;
using System.Data.Common;
using System.Net;
using Microsoft.Azure.Cosmos;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// An error returned by Cosmos DB while the provider ran a statement or opened a connection. ADO.NET consumers
/// such as YesSql expect a data store failure to be a <see cref="DbException"/>, so Cosmos errors are surfaced as
/// this type. The original <see cref="CosmosException"/> is the <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class CosmosDbException : DbException
{
    internal CosmosDbException(CosmosException inner)
        : base(inner.Message, inner)
    {
        StatusCode = inner.StatusCode;
        SubStatusCode = inner.SubStatusCode;
        RetryAfter = inner.RetryAfter;
    }

    /// <summary>The HTTP status code of the failed Cosmos request, for example 429 when throttled.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The Cosmos sub-status code of the failed request.</summary>
    public int SubStatusCode { get; }

    /// <summary>How long Cosmos asked the caller to wait before retrying, when it said so.</summary>
    public TimeSpan? RetryAfter { get; }
}
