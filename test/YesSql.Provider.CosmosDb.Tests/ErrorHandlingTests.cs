using System.Data.Common;
using System.Net;
using Microsoft.Azure.Cosmos;
using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Cosmos failures reach ADO.NET callers as <see cref="DbException"/>, with the Cosmos error preserved.
/// </summary>
public class ErrorHandlingTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class PersonByName : MapIndex
    {
        public string Name { get; set; } = string.Empty;
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<PersonByName>().Map(p => new PersonByName { Name = p.Name });
    }

    private static async Task<IStore> NewStoreAsync()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_errors"))));
        store.RegisterIndexes<PersonIndexProvider>();
        return store;
    }

    [Fact]
    public async Task A_query_Cosmos_rejects_throws_a_DbException_that_keeps_the_Cosmos_error()
    {
        var store = await NewStoreAsync();
        await using var session = store.CreateSession();
        await session.SaveAsync(new Person { Name = "Alice" });
        await session.FlushAsync();

        var exception = await Assert.ThrowsAnyAsync<DbException>(
            () => session.Query<Person>().With<PersonByName>().Where("ThisColumnDoesNotExist = 1").ListAsync());

        var cosmos = Assert.IsType<CosmosDbException>(exception);
        Assert.Equal(HttpStatusCode.BadRequest, cosmos.StatusCode);
        Assert.IsAssignableFrom<CosmosException>(cosmos.InnerException);

        // YesSql cancels the session when a query fails, which rolls back the flushed write.
        Assert.Null(session.CurrentTransaction);
    }

    [Fact]
    public async Task A_missing_container_surfaces_as_a_DbException_when_creation_is_disabled()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_nocreate2");

        var exception = await Assert.ThrowsAsync<CosmosDbException>(
            () => StoreFactory.CreateAndInitializeAsync(
                new Configuration().UseCosmosDb(Emulator.Options(databaseId, createIfNotExists: false))));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }
}
