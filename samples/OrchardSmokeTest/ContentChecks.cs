using System.Diagnostics;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Settings;
using OrchardCore.ContentManagement.Records;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Scope;
using ISession = YesSql.ISession;
using YesSql;

/// <summary>
/// Runs the content operations of a real Orchard Core site through the tenant's services, so that Orchard's content
/// manager, its content index and YesSql all work on the Cosmos DB provider. Each step runs in its own shell scope, as a
/// request does, and the result of every check is returned for the caller to read.
/// </summary>
/// <remarks>
/// It covers creating, publishing, editing (a new version), unpublishing and removing content items, queries over the
/// content index with ordering and paging, concurrent creation, and discarding a request's changes. It does not drive the
/// admin pages, so authentication, antiforgery and rendering are outside what it shows.
/// </remarks>
internal static class ContentChecks
{
    private const string ContentType = "SmokeArticle";

    public sealed record Check(string Name, bool Ok, string Detail);

    public static async Task<IReadOnlyList<Check>> RunAsync(IShellHost shellHost)
    {
        var checks = new List<Check>();
        var clock = Stopwatch.StartNew();

        async Task Step(string name, Func<ShellScope, Task> body)
        {
            var started = clock.ElapsedMilliseconds;
            try
            {
                var scope = await shellHost.GetScopeAsync(ShellSettings.DefaultShellName);
                await scope.UsingAsync(body);
            }
            catch (Exception ex)
            {
                checks.Add(new Check(name, false, $"threw {ex.GetType().Name}: {ex.Message}"));
                return;
            }

            checks.Add(new Check(name + " (ran)", true, $"{clock.ElapsedMilliseconds - started} ms"));
        }

        void Expect(string name, bool ok, string detail) => checks.Add(new Check(name, ok, detail));

        static IContentManager Manager(ShellScope scope) => scope.ServiceProvider.GetRequiredService<IContentManager>();
        static ISession Session(ShellScope scope) => scope.ServiceProvider.GetRequiredService<ISession>();

        var ids = new List<string>();

        await Step("define the content type", async scope =>
        {
            var definitions = scope.ServiceProvider.GetRequiredService<IContentDefinitionManager>();
            await definitions.AlterTypeDefinitionAsync(ContentType, type => type.DisplayedAs("Smoke article").Draftable().Versionable());
        });

        await Step("the content type definition is stored", async scope =>
        {
            var definitions = scope.ServiceProvider.GetRequiredService<IContentDefinitionManager>();
            var definition = await definitions.GetTypeDefinitionAsync(ContentType);
            Expect("the content type definition is stored", definition is not null, definition is null ? "not found" : definition.DisplayName);
        });

        // Five items, each created and published in its own request.
        for (var i = 1; i <= 5; i++)
        {
            var number = i;
            await Step($"create and publish item {number}", async scope =>
            {
                var manager = Manager(scope);
                var item = await manager.NewAsync(ContentType);
                item.DisplayText = $"Article {number}";
                await manager.CreateAsync(item, VersionOptions.Published);
                lock (ids)
                {
                    ids.Add(item.ContentItemId);
                }
            });
        }

        await Step("the items are published and latest", async scope =>
        {
            var session = Session(scope);
            var published = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Published).CountAsync();
            var latest = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest).CountAsync();
            Expect("five published", published == 5, $"published={published}");
            Expect("five latest", latest == 5, $"latest={latest}");

            var manager = Manager(scope);
            var loaded = await manager.GetAsync(ids[0]);
            Expect("an item loads by id", loaded?.DisplayText == "Article 1", loaded?.DisplayText ?? "null");
            var many = await manager.GetAsync(ids, VersionOptions.Published);
            Expect("items load by ids", many.Count() == 5, $"count={many.Count()}");
        });

        await Step("edit item 1 as a draft", async scope =>
        {
            var manager = Manager(scope);
            var draft = await manager.GetAsync(ids[0], VersionOptions.DraftRequired);
            draft.DisplayText = "Article 1 edited";
            await manager.UpdateAsync(draft);
        });

        await Step("the draft is a second version", async scope =>
        {
            var session = Session(scope);
            var rows = (await session.QueryIndex<ContentItemIndex>(x => x.ContentItemId == ids[0]).ListAsync()).ToList();
            Expect("item 1 has two versions", rows.Count == 2, $"versions={rows.Count}");
            Expect("the published version is the original", rows.Any(r => r.Published && r.DisplayText == "Article 1" && !r.Latest),
                string.Join(", ", rows.Select(r => $"{r.DisplayText}:p={r.Published}:l={r.Latest}")));
            Expect("the draft is the latest", rows.Any(r => r.Latest && !r.Published && r.DisplayText == "Article 1 edited"),
                string.Join(", ", rows.Select(r => $"{r.DisplayText}:p={r.Published}:l={r.Latest}")));

            var manager = Manager(scope);
            var published = await manager.GetAsync(ids[0], VersionOptions.Published);
            var latest = await manager.GetAsync(ids[0], VersionOptions.Latest);
            Expect("the published version is unchanged", published?.DisplayText == "Article 1", published?.DisplayText ?? "null");
            Expect("the latest version is the draft", latest?.DisplayText == "Article 1 edited", latest?.DisplayText ?? "null");
        });

        await Step("publish the draft", async scope =>
        {
            var manager = Manager(scope);
            var draft = await manager.GetAsync(ids[0], VersionOptions.Latest);
            await manager.PublishAsync(draft);
        });

        await Step("the draft replaced the published version", async scope =>
        {
            var session = Session(scope);
            var rows = (await session.QueryIndex<ContentItemIndex>(x => x.ContentItemId == ids[0]).ListAsync()).ToList();
            var published = rows.Where(r => r.Published).ToList();
            Expect("one published version", published.Count == 1 && published[0].DisplayText == "Article 1 edited",
                string.Join(", ", rows.Select(r => $"{r.DisplayText}:p={r.Published}:l={r.Latest}")));
            Expect("one latest version", rows.Count(r => r.Latest) == 1, $"latest={rows.Count(r => r.Latest)}");
        });

        await Step("unpublish item 2", async scope =>
        {
            var manager = Manager(scope);
            await manager.UnpublishAsync((await manager.GetAsync(ids[1]))!);
        });

        await Step("item 2 is unpublished and still latest", async scope =>
        {
            var session = Session(scope);
            var published = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Published).CountAsync();
            var latest = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest).CountAsync();
            Expect("four published", published == 4, $"published={published}");
            Expect("five latest", latest == 5, $"latest={latest}");
        });

        await Step("remove item 3", async scope =>
        {
            var manager = Manager(scope);
            await manager.RemoveAsync((await manager.GetAsync(ids[2], VersionOptions.Latest))!);
        });

        await Step("item 3 is gone", async scope =>
        {
            var session = Session(scope);
            var latest = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest).CountAsync();
            Expect("four latest", latest == 4, $"latest={latest}");
            var gone = await Manager(scope).GetAsync(ids[2], VersionOptions.Latest);
            Expect("item 3 does not load", gone is null, gone?.DisplayText ?? "null");
        });

        await Step("ordered and paged queries", async scope =>
        {
            var session = Session(scope);
            var query = () => session.Query<ContentItem, ContentItemIndex>(x => x.ContentType == ContentType && x.Latest);

            var titles = (await query().OrderBy(x => x.DisplayText).ListAsync()).Select(x => x.DisplayText).ToList();
            Expect("ordered by title", titles.SequenceEqual(titles.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) && titles.Count == 4, string.Join("|", titles));

            var page = (await query().OrderBy(x => x.DisplayText).Skip(1).Take(2).ListAsync()).Select(x => x.DisplayText).ToList();
            Expect("a page of two after the first", page.SequenceEqual(titles.Skip(1).Take(2)), string.Join("|", page));

            var byDate = (await query().OrderByDescending(x => x.CreatedUtc).ListAsync()).Select(x => x.DisplayText).ToList();
            Expect("ordered by creation date, newest first", byDate.Count == 4 && byDate[0] == "Article 5", string.Join("|", byDate));

            var count = await query().CountAsync();
            Expect("counted", count == 4, $"count={count}");

            var first = await query().OrderBy(x => x.DisplayText).FirstOrDefaultAsync();
            Expect("first by title", first?.DisplayText == titles[0], first?.DisplayText ?? "null");
        });

        // Twenty requests creating items at the same time.
        var before = 0;
        await Step("count before the concurrent creates", async scope =>
        {
            before = await Session(scope).QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest).CountAsync();
        });

        var concurrent = Enumerable.Range(0, 20).Select(async n =>
        {
            var scope = await shellHost.GetScopeAsync(ShellSettings.DefaultShellName);
            await scope.UsingAsync(async s =>
            {
                var manager = Manager(s);
                var item = await manager.NewAsync(ContentType);
                item.DisplayText = $"Concurrent {n:D2}";
                await manager.CreateAsync(item, VersionOptions.Published);
            });
        }).ToList();
        try
        {
            await Task.WhenAll(concurrent);
            Expect("twenty concurrent creates succeed", true, "20 requests");
        }
        catch (Exception ex)
        {
            Expect("twenty concurrent creates succeed", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        await Step("the concurrent items are all there", async scope =>
        {
            var session = Session(scope);
            var rows = (await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest && x.DisplayText.StartsWith("Concurrent")).ListAsync()).ToList();
            Expect("twenty items", rows.Count == 20, $"items={rows.Count}");
            Expect("twenty distinct content item ids", rows.Select(r => r.ContentItemId).Distinct().Count() == 20, $"distinct={rows.Select(r => r.ContentItemId).Distinct().Count()}");
            var total = await session.QueryIndex<ContentItemIndex>(x => x.ContentType == ContentType && x.Latest).CountAsync();
            Expect("the total grew by twenty", total == before + 20, $"before={before} total={total}");
        });

        // A request that fails validation and cancels the session must leave nothing behind, even though the query inside
        // it had already written its changes to Cosmos.
        await Step("a cancelled request", async scope =>
        {
            var manager = Manager(scope);
            var session = Session(scope);
            var item = await manager.NewAsync(ContentType);
            item.DisplayText = "Cancelled";
            await manager.CreateAsync(item, VersionOptions.Published);
            var seen = await session.QueryIndex<ContentItemIndex>(x => x.DisplayText == "Cancelled").CountAsync();
            Expect("the cancelled item is visible inside its own request", seen == 1, $"seen={seen}");
            await session.CancelAsync();
        });

        await Step("the cancelled request left nothing", async scope =>
        {
            var session = Session(scope);
            var rows = await session.QueryIndex<ContentItemIndex>(x => x.DisplayText == "Cancelled").CountAsync();
            Expect("no index rows of the cancelled item", rows == 0, $"rows={rows}");
            var documents = (await session.Query<ContentItem, ContentItemIndex>(x => x.DisplayText == "Cancelled").ListAsync()).Count();
            Expect("no documents of the cancelled item", documents == 0, $"documents={documents}");
        });

        // A request that throws.
        try
        {
            var scope = await shellHost.GetScopeAsync(ShellSettings.DefaultShellName);
            await scope.UsingAsync(async s =>
            {
                var manager = Manager(s);
                var item = await manager.NewAsync(ContentType);
                item.DisplayText = "Failed";
                await manager.CreateAsync(item, VersionOptions.Published);
                await Session(s).QueryIndex<ContentItemIndex>(x => x.DisplayText == "Failed").CountAsync();
                throw new InvalidOperationException("simulated failure");
            });
            Expect("a failing request throws", false, "it did not throw");
        }
        catch (InvalidOperationException)
        {
            Expect("a failing request throws", true, "InvalidOperationException");
        }

        await Step("the failed request left nothing", async scope =>
        {
            var rows = await Session(scope).QueryIndex<ContentItemIndex>(x => x.DisplayText == "Failed").CountAsync();
            Expect("no index rows of the failed item", rows == 0, $"rows={rows}");
        });

        return checks;
    }
}
