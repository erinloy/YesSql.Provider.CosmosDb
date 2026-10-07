using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// A query ordered by an index column is sorted in the client, case-insensitively. The values of a date column come back
/// from Cosmos as dates, not as the text that was stored, so they have to be compared as moments in time. Compared as the
/// culture's text they lose their fractions of a second and sort "9:59 AM" after "10:00 AM".
/// </summary>
public class DateOrderingTests
{
    public class Meeting
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    public class MeetingByTime : MapIndex
    {
        public string Title { get; set; } = string.Empty;
        public DateTime Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    public class MeetingIndexProvider : IndexProvider<Meeting>
    {
        public override void Describe(DescribeContext<Meeting> context)
            => context.For<MeetingByTime>().Map(m => new MeetingByTime { Title = m.Title, Start = m.Start, End = m.End });
    }

    private static DateTime Utc(int hour, int minute, int second, int millisecond)
        => new(2021, 1, 20, hour, minute, second, millisecond, DateTimeKind.Utc);

    // Titles are in chronological order of Start, and the rows are saved in a different order.
    private static readonly (string Title, DateTime Start)[] Chronological =
    {
        ("a", Utc(0, 0, 0, 500)),
        ("b", Utc(9, 59, 59, 100)),
        ("c", Utc(9, 59, 59, 900)),
        ("d", Utc(10, 0, 0, 0)),
        ("e", Utc(11, 30, 0, 0)),
        ("f", Utc(12, 0, 0, 0)),
        ("g", Utc(21, 0, 0, 0)),
    };

    private static async Task<IStore> SeedAsync(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_dateorder"), strategy: strategy)));
        store.RegisterIndexes<MeetingIndexProvider>();

        await using var session = store.CreateSession();
        foreach (var index in new[] { 4, 1, 6, 0, 3, 5, 2 })
        {
            var (title, start) = Chronological[index];
            await session.SaveAsync(new Meeting { Title = title, Start = start, End = new DateTimeOffset(start.AddHours(1), TimeSpan.Zero) });
        }

        await session.SaveChangesAsync();
        return store;
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_date_column_orders_chronologically_ascending_and_descending(PartitionStrategy strategy)
    {
        var store = await SeedAsync(strategy);
        var expected = Chronological.Select(m => m.Title).ToList();

        await using var session = store.CreateSession();

        var ascending = (await session.Query<Meeting, MeetingByTime>().OrderBy(x => x.Start).ListAsync()).Select(m => m.Title).ToList();
        Assert.Equal(expected, ascending);

        var descending = (await session.Query<Meeting, MeetingByTime>().OrderByDescending(x => x.Start).ListAsync()).Select(m => m.Title).ToList();
        Assert.Equal(Enumerable.Reverse(expected), descending);

        var byEnd = (await session.Query<Meeting, MeetingByTime>().OrderBy(x => x.End).ListAsync()).Select(m => m.Title).ToList();
        Assert.Equal(expected, byEnd);
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_page_of_a_date_ordered_query_is_the_right_page(PartitionStrategy strategy)
    {
        var store = await SeedAsync(strategy);
        var expected = Chronological.Select(m => m.Title).ToList();

        await using var session = store.CreateSession();

        var page = (await session.Query<Meeting, MeetingByTime>().OrderBy(x => x.Start).Skip(1).Take(3).ListAsync()).Select(m => m.Title).ToList();
        Assert.Equal(expected.Skip(1).Take(3), page);

        var newest = await session.Query<Meeting, MeetingByTime>().OrderByDescending(x => x.Start).FirstOrDefaultAsync();
        Assert.Equal("g", newest!.Title);
    }
}
