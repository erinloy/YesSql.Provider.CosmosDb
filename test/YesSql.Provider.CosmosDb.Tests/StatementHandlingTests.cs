using System.Data;
using Newtonsoft.Json.Linq;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Statements the provider recognizes but cannot carry out must fail, not silently do nothing.
/// </summary>
public class StatementHandlingTests
{
    public class Doc
    {
        public int Id { get; set; }
        public string Payload { get; set; } = string.Empty;
    }

    private static async Task<IStore> NewStoreAsync()
        => await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_stmt"))));

    private static async Task<int> ExecuteAsync(IStore store, string sql, Action<IDbCommand>? configure = null)
    {
        await using var connection = store.Configuration.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_malformed_RenameColumn_statement_is_rejected()
    {
        var store = await NewStoreAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteAsync(store, "renamecolumn [Table] [OnlyOneColumn]"));
    }

    [Fact]
    public async Task REPLACE_with_a_NULL_search_value_is_rejected_and_leaves_content_alone()
    {
        var store = await NewStoreAsync();
        long id;
        await using (var session = store.CreateSession())
        {
            var doc = new Doc { Payload = "keep me" };
            await session.SaveAsync(doc);
            await session.SaveChangesAsync();
            id = doc.Id;
        }

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ExecuteAsync(store, "UPDATE [Document] SET [Content] = REPLACE([Content], NULL, 'x')"));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ExecuteAsync(store, "UPDATE [Document] SET [Content] = REPLACE([Content], @from, 'x')", command =>
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "from";
                parameter.Value = DBNull.Value;
                command.Parameters.Add(parameter);
            }));

        await using var check = store.CreateSession();
        Assert.Equal("keep me", (await check.GetAsync<Doc>(id))!.Payload);
    }

    [Fact]
    public async Task REPLACE_with_an_empty_search_value_changes_nothing()
    {
        var store = await NewStoreAsync();
        long id;
        await using (var session = store.CreateSession())
        {
            var doc = new Doc { Payload = "unchanged" };
            await session.SaveAsync(doc);
            await session.SaveChangesAsync();
            id = doc.Id;
        }

        Assert.Equal(0, await ExecuteAsync(store, "UPDATE [Document] SET [Content] = REPLACE([Content], '', 'x')"));

        await using var check = store.CreateSession();
        Assert.Equal("unchanged", (await check.GetAsync<Doc>(id))!.Payload);
    }
}
