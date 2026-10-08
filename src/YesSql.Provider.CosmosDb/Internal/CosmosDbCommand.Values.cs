using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// Conversion between ADO.NET parameter values, JSON tokens and result rows.
internal sealed partial class CosmosDbCommand
{
    private static object?[] ToRow(JObject item) =>
    [
        item["Id"]?.ToObject<long>(),
        item["Type"]?.ToObject<string>(),
        item["Content"]?.ToObject<string>(),
        item["Version"]?.ToObject<long>(),
    ];

    private static JToken ToToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        // JSON has no binary type; wrap byte[] self-descriptively so reads can recover it as byte[]
        // (a bare base64 string would come back as a string and fail the byte[] cast).
        byte[] bytes => new JObject { ["$b64"] = Convert.ToBase64String(bytes) },
        _ => JToken.FromObject(AsUtc(value)!),
    };

    // A moment in time is stored and queried as a UTC instant ("...Z"), never with an offset. Cosmos DB compares
    // DateTimeToTimestamp(c.x) wrongly in a WHERE clause for a stored offset east of +01:00 ("...+05:30" is
    // never equal to its own instant), so a value written with such an offset could not be found by a query.
    // A DateTime of unspecified kind is left as it is, because it carries no offset.
    private static object? AsUtc(object? value) => value switch
    {
        DateTimeOffset moment => moment.UtcDateTime,
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        _ => value,
    };

    // Reverse of ToToken for reading column values: recover wrapped byte[]; otherwise the raw CLR value.
    private static object? FromToken(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token is JObject obj && obj["$b64"] is { } b64)
        {
            return Convert.FromBase64String(b64.Value<string>()!);
        }

        return token.ToObject<object>();
    }

    private object? Param(string name)
        => TryParam(name, out var value) ? value : throw new InvalidOperationException($"Parameter '{name}' not found for: {CommandText}");

    private bool TryParam(string name, out object? value)
    {
        foreach (DbParameter p in _parameters)
        {
            if (string.Equals(p.ParameterName.TrimStart('@'), name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value is DBNull ? null : p.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
