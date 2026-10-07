using Newtonsoft.Json.Linq;
using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// A moment in time is stored as a UTC instant. Cosmos DB compares <c>DateTimeToTimestamp</c> wrongly in a WHERE clause for
/// a stored offset east of +01:00, so a row written with such an offset could not be found again on a real account.
/// </summary>
public class DateStorageTests
{
    public class Appointment
    {
        public int Id { get; set; }
        public DateTimeOffset At { get; set; }
        public DateTime Local { get; set; }
    }

    public class AppointmentByTime : MapIndex
    {
        public DateTimeOffset At { get; set; }
        public DateTime Local { get; set; }
    }

    public class AppointmentIndexProvider : IndexProvider<Appointment>
    {
        public override void Describe(DescribeContext<Appointment> context)
            => context.For<AppointmentByTime>().Map(a => new AppointmentByTime { At = a.At, Local = a.Local });
    }

    [Theory]
    [InlineData("+05:30")]
    [InlineData("+05:45")]
    [InlineData("+13:00")]
    [InlineData("-03:30")]
    [InlineData("+00:00")]
    public async Task A_DateTimeOffset_is_stored_as_a_UTC_instant_and_is_found_by_it(string offset)
    {
        var databaseId = Emulator.NewDatabaseId("yessql_dates");
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Emulator.Options(databaseId)));
        store.RegisterIndexes<AppointmentIndexProvider>();

        var at = new DateTimeOffset(2021, 1, 20, 9, 30, 0, TimeSpan.Parse(offset.TrimStart('+')));
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Appointment { At = at, Local = new DateTime(2021, 1, 20, 9, 30, 0, DateTimeKind.Local) });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            var found = await session.Query<Appointment, AppointmentByTime>(x => x.At == at).ListAsync();
            Assert.Single(found);

            // the same instant written with another offset is the same value
            var sameInstant = at.ToOffset(TimeSpan.FromHours(-8));
            Assert.Single(await session.Query<Appointment, AppointmentByTime>(x => x.At == sameInstant).ListAsync());
            Assert.Empty(await session.Query<Appointment, AppointmentByTime>(x => x.At == at.AddMinutes(1)).ListAsync());
            Assert.Single(await session.Query<Appointment, AppointmentByTime>(x => x.At >= at && x.At <= at).ListAsync());

            var row = (await session.QueryIndex<AppointmentByTime>().ListAsync()).Single();
            Assert.Equal(at, row.At);
        }

        var container = Emulator.NewClient().GetContainer(databaseId, "yessql");
        using var iterator = container.GetItemQueryIterator<string>(
            "SELECT VALUE c.At FROM c WHERE IS_DEFINED(c.At)",
            requestOptions: new Microsoft.Azure.Cosmos.QueryRequestOptions { PartitionKey = new Microsoft.Azure.Cosmos.PartitionKey("AppointmentByTime") });
        var stored = (await iterator.ReadNextAsync()).Single();
        Assert.EndsWith("Z", stored);
        Assert.Equal(at.UtcDateTime, DateTime.Parse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task A_local_DateTime_is_stored_as_UTC()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_dates");
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Emulator.Options(databaseId)));
        store.RegisterIndexes<AppointmentIndexProvider>();

        var local = new DateTime(2021, 1, 20, 9, 30, 0, DateTimeKind.Local);
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Appointment { At = DateTimeOffset.UnixEpoch, Local = local });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Single(await session.Query<Appointment, AppointmentByTime>(x => x.Local == local).ListAsync());
        }
    }
}
