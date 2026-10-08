using System.Data.Common;
using Microsoft.Azure.Cosmos;
using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// What the provider refuses instead of doing something other than what the SQL says: an INSERT of an id that is taken,
/// an UPDATE of a row that is not there, a condition the provider cannot apply, and the joins that are not inner joins.
/// </summary>
public class StrictSemanticsTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class ByName : MapIndex
    {
        public string Name { get; set; } = string.Empty;
    }

    public class ByLength : MapIndex
    {
        public int Length { get; set; }
    }

    public class NameCount : ReduceIndex
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    // Both document types produce the same indexes, so an index query has to tell their documents apart by type. A document
    // with an empty name has no ByName row.
    public class PersonIndexes : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
        {
            context.For<ByName>().Map(p => p.Name.Length == 0 ? null! : new ByName { Name = p.Name });
            context.For<ByLength>().Map(p => new ByLength { Length = p.Name.Length });
            context.For<NameCount, string>()
                .Map(p => new NameCount { Name = p.Name, Count = 1 })
                .Group(p => p.Name)
                .Reduce(group => new NameCount { Name = group.First().Name, Count = group.Sum(x => x.Count) })
                .Delete((index, map) =>
                {
                    index.Count -= map.Sum(x => x.Count);
                    return index.Count > 0 ? index : null!;
                });
        }
    }

    public class AnimalIndexes : IndexProvider<Animal>
    {
        public override void Describe(DescribeContext<Animal> context)
        {
            context.For<ByName>().Map(a => new ByName { Name = a.Name });
            context.For<ByLength>().Map(a => new ByLength { Length = a.Name.Length });
            context.For<NameCount, string>()
                .Map(a => new NameCount { Name = a.Name, Count = 1 })
                .Group(a => a.Name)
                .Reduce(group => new NameCount { Name = group.First().Name, Count = group.Sum(x => x.Count) })
                .Delete((index, map) =>
                {
                    index.Count -= map.Sum(x => x.Count);
                    return index.Count > 0 ? index : null!;
                });
        }
    }

    private static async Task<IStore> NewStoreAsync(PartitionStrategy strategy, string? database = null, bool blockIds = false)
    {
        var configuration = new Configuration().UseCosmosDb(Emulator.Options(database ?? Emulator.NewDatabaseId("yessql_strict"), strategy: strategy));
        var store = await StoreFactory.CreateAndInitializeAsync(blockIds ? configuration.UseBlockIdGenerator() : configuration.UseDefaultIdGenerator());
        store.RegisterIndexes<PersonIndexes>();
        store.RegisterIndexes<AnimalIndexes>();
        return store;
    }

    private static async Task<DbCommand> CommandAsync(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await Task.CompletedTask;
        return command;
    }

    private static async Task<int> ExecuteAsync(IStore store, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = store.Configuration.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = await CommandAsync(connection, sql, parameters);
        command.Transaction = transaction;
        var affected = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return affected;
    }

    private static async Task<List<object?>> QueryFirstColumnAsync(IStore store, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = store.Configuration.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = await CommandAsync(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<object?>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0));
        }

        return values;
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Two_processes_that_issue_the_same_document_id_cannot_overwrite_each_other(PartitionStrategy strategy)
    {
        // Each store seeds its default id generator from the largest stored id when it starts, so two that start before either
        // has saved will issue the same ids, as two processes would.
        var database = Emulator.NewDatabaseId("yessql_strict");
        var first = await NewStoreAsync(strategy, database);
        var second = await NewStoreAsync(strategy, database);

        await using (var session = first.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "first" });
            await session.SaveChangesAsync();
        }

        var thrown = await Assert.ThrowsAsync<CosmosDbException>(async () =>
        {
            await using var session = second.CreateSession();
            await session.SaveAsync(new Person { Name = "second" });
            await session.SaveChangesAsync();
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, thrown.StatusCode);

        // The document that was there is still there and is what it was, and the failed save left no index row behind.
        await using var check = first.CreateSession();
        var people = (await check.Query<Person>().ListAsync()).ToList();
        Assert.Equal("first", Assert.Single(people).Name);
        Assert.Equal(1, await check.Query<Person, ByName>().CountAsync());
        Assert.Equal(1, await check.Query<Person, ByName>(x => x.Name == "first").CountAsync());
        Assert.Equal(0, await check.Query<Person, ByName>(x => x.Name == "second").CountAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Two_processes_with_the_block_id_generator_never_issue_the_same_id(PartitionStrategy strategy)
    {
        var database = Emulator.NewDatabaseId("yessql_strict");
        var first = await NewStoreAsync(strategy, database, blockIds: true);
        var second = await NewStoreAsync(strategy, database, blockIds: true);

        async Task SaveAsync(IStore store, string prefix)
        {
            for (var i = 0; i < 30; i++)
            {
                await using var session = store.CreateSession();
                await session.SaveAsync(new Person { Name = $"{prefix}{i}" });
                await session.SaveChangesAsync();
            }
        }

        await Task.WhenAll(SaveAsync(first, "a"), SaveAsync(second, "b"));

        await using var check = first.CreateSession();
        var people = (await check.Query<Person>().ListAsync()).ToList();
        Assert.Equal(60, people.Count);
        Assert.Equal(60, people.Select(p => p.Id).Distinct().Count());
        Assert.Equal(60, await check.Query<Person, ByName>().CountAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task The_block_id_generator_continues_after_a_restart(PartitionStrategy strategy)
    {
        var database = Emulator.NewDatabaseId("yessql_strict");
        var before = await NewStoreAsync(strategy, database, blockIds: true);
        await using (var session = before.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "one" });
            await session.SaveChangesAsync();
        }

        var after = await NewStoreAsync(strategy, database, blockIds: true);
        await using (var session = after.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "two" });
            await session.SaveChangesAsync();
        }

        await using var check = after.CreateSession();
        var ids = (await check.Query<Person>().ListAsync()).Select(p => p.Id).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task An_update_of_a_row_that_is_not_there_changes_nothing(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        var affected = await ExecuteAsync(store, "UPDATE [Document] SET [Content] = @Content, [Version] = @Version WHERE [Id] = @Id",
            ("@Id", 9999L), ("@Content", "{}"), ("@Version", 1L));

        Assert.Equal(0, affected);
        await using var check = store.CreateSession();
        Assert.Empty(await check.GetAsync<Person>(new long[] { 9999 }));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_query_of_the_document_table_refuses_a_condition_it_would_ignore(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        var thrown = await Assert.ThrowsAsync<NotSupportedException>(
            () => QueryFirstColumnAsync(store, "SELECT [Id] FROM [Document] WHERE [Type] = @Type AND [Version] > 1", ("@Type", "x")));
        Assert.Contains("Type", thrown.Message);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => QueryFirstColumnAsync(store, "SELECT [Id] FROM [Document] WHERE [Id] = @Id AND [Version] = 1", ("@Id", 1L)));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_query_of_the_document_table_can_filter_by_type_and_by_key(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "a" });
            await session.SaveAsync(new Animal { Name = "b" });
            await session.SaveChangesAsync();
        }

        await using var check = store.CreateSession();
        var person = Assert.Single(await check.Query<Person>().ListAsync());
        var animal = Assert.Single(await check.Query<Animal>().ListAsync());
        Assert.Equal("a", person.Name);
        Assert.Equal("b", animal.Name);

        Assert.Single(await check.GetAsync<Person>(new long[] { person.Id }));
        Assert.Single(await check.GetAsync<Animal>(new long[] { animal.Id }));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_query_over_several_indexes_returns_only_documents_of_the_type_asked_for(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "abc" });
            await session.SaveAsync(new Animal { Name = "abc" });
            await session.SaveAsync(new Animal { Name = "abc" });
            await session.SaveChangesAsync();
        }

        await using var check = store.CreateSession();
        var people = check.Query<Person>().With<ByName>(x => x.Name == "abc").With<ByLength>(x => x.Length == 3);
        Assert.Single(await people.ListAsync());
        Assert.Equal(1, await people.CountAsync());

        var animals = check.Query<Animal>().With<ByName>(x => x.Name == "abc").With<ByLength>(x => x.Length == 3);
        Assert.Equal(2, (await animals.ListAsync()).Count());
        Assert.Equal(2, await animals.CountAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_query_over_a_reduce_index_returns_only_documents_of_the_type_asked_for(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "abc" });
            await session.SaveAsync(new Animal { Name = "abc" });
            await session.SaveChangesAsync();
        }

        await using var check = store.CreateSession();
        Assert.Single(await check.Query<Person>().With<NameCount>(x => x.Name == "abc").ListAsync());
        Assert.Equal(1, await check.Query<Person>().With<NameCount>(x => x.Name == "abc").CountAsync());
        Assert.Single(await check.Query<Animal>().With<NameCount>(x => x.Name == "abc").ListAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_reduce_index_joined_to_a_map_index_applies_the_conditions_of_each(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "abc" });
            await session.SaveAsync(new Person { Name = "abcd" });
            await session.SaveChangesAsync();
        }

        await using var check = store.CreateSession();
        var matching = check.Query<Person>().With<NameCount>(x => x.Name == "abcd").With<ByLength>(x => x.Length == 4);
        Assert.Single(await matching.ListAsync());

        // The map index has no row with this length, so nothing matches, whatever the reduce index holds.
        var none = check.Query<Person>().With<NameCount>(x => x.Name == "abcd").With<ByLength>(x => x.Length == 3);
        Assert.Empty(await none.ListAsync());
        Assert.Equal(0, await none.CountAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Counts_over_inner_left_and_right_joins_count_the_joined_rows(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "a" });
            await session.SaveAsync(new Person { Name = "b" });
            await session.SaveAsync(new Person { Name = string.Empty }); // a document with no ByName row
            await session.SaveChangesAsync();
        }

        const string join = " JOIN [ByName] AS a ON a.[DocumentId] = d.[Id]";
        Assert.Equal(2L, Convert.ToInt64((await QueryFirstColumnAsync(store, "SELECT count(1) FROM [Document] AS d INNER" + join)).Single()));
        Assert.Equal(2L, Convert.ToInt64((await QueryFirstColumnAsync(store, "SELECT count(1) FROM [Document] AS d RIGHT" + join)).Single()));
        Assert.Equal(3L, Convert.ToInt64((await QueryFirstColumnAsync(store, "SELECT count(1) FROM [Document] AS d LEFT" + join)).Single()));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_left_join_that_the_provider_cannot_count_exactly_is_refused(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        const string join = " JOIN [ByName] AS a ON a.[DocumentId] = d.[Id]";

        await Assert.ThrowsAsync<NotSupportedException>(
            () => QueryFirstColumnAsync(store, "SELECT count(1) FROM [Document] AS d LEFT" + join + " WHERE a.[Name] = @n", ("@n", "a")));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => QueryFirstColumnAsync(store, "SELECT d.[Id] FROM [Document] AS d LEFT" + join));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_count_of_joined_rows_counts_each_index_row_of_a_document(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "a" });
            await session.SaveChangesAsync();
        }

        // One document, one ByName row: COUNT(1) and COUNT(DISTINCT) agree, and a condition on the index applies to both.
        Assert.Equal(1L, Convert.ToInt64((await QueryFirstColumnAsync(store,
            "SELECT count(1) FROM [Document] AS d INNER JOIN [ByName] AS a ON a.[DocumentId] = d.[Id] WHERE a.[Name] = @n", ("@n", "a"))).Single()));
        Assert.Equal(0L, Convert.ToInt64((await QueryFirstColumnAsync(store,
            "SELECT count(1) FROM [Document] AS d INNER JOIN [ByName] AS a ON a.[DocumentId] = d.[Id] WHERE a.[Name] = @n", ("@n", "z"))).Single()));
    }
}
