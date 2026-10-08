# What the provider assumes about YesSql

YesSql does not know it is talking to Cosmos DB. It builds SQL text, hands it to Dapper and ADO.NET, and reads results back by column name. The provider answers that SQL without a SQL engine, so it depends on the *shape* of what YesSql generates, in a way a relational provider does not. This page lists those dependencies, so that a change to YesSql can be checked against them, and says what would catch a change.

The statement of each assumption is about YesSql 5.4.7, which the package is built against. YesSql 6.0.0 passes the same tests; where it differs, the row says so.

## The extension points

YesSql reads three properties of `IConfiguration` to reach a database: `ConnectionFactory`, `SqlDialect` and `CommandInterpreter`. `UseCosmosDb` sets all three and nothing else in YesSql changes. The provider does not use reflection into YesSql or any internal type; it implements `ISqlDialect` (by deriving from `BaseDialect`), `ICommandInterpreter` and `IConnectionFactory`, and the ADO.NET classes the factory returns.

## Assumptions

| # | The provider assumes | YesSql does this in | The provider relies on it in | Pinned by |
| --- | --- | --- | --- | --- |
| 1 | Parameters are named after the columns they fill. | `CreateDocumentCommand`, `UpdateDocumentCommand` and `IndexCommand` pass the document or the index object to Dapper, so the parameters are `@Id`, `@Type`, `@Content`, `@Version` and one for each index property. | An `INSERT` stores each parameter as a field under its name. An `UPDATE` does not: it sets the columns of its `SET` clause. | The conformance suites |
| 2 | Commands run one statement at a time. | With `SupportsBatching`, `BatchCommand` joins statements and suffixes parameter names (`@Id_1`, `@Id_2`). | `CosmosDbDialect.SupportsBatching` is `false`, because a batched command would break assumption 1. | `StatementShapeTests`, the conformance suites |
| 3 | An index row is inserted by a statement that ends in `RETURNING [Id]` and is run as a scalar. | `IndexCommand.Inserts` appends `dialect.IdentitySelectString`; `CreateIndexCommand` runs it with `ExecuteScalarAsync` to read the id. | The insert allocates the row id from the table's counter and returns it. | The conformance suites |
| 4 | A reduce index's link row is a non-query `INSERT` with a `DocumentId` parameter and no `Type` or `Content`. | `CreateIndexCommand` and `UpdateIndexCommand` insert `([<Index>Id], [DocumentId])` into the bridge table `<index table>_<document table>`. | The link item gets the id `<table>:<index id>:<document id>`. | The conformance suites |
| 5 | A table whose name ends in `Document` is a document table, and one that ends in `Identifiers` is the table of `DbBlockIdGenerator`. | `DefaultTableNameConvention.GetDocumentTable` (`Document`, or `<collection>_Document`, with the table prefix put in front); `DbBlockIdGenerator.TableName` (`Identifiers`). | `SelectShape.IsDocumentTable` and `IsIdentifierTable` route statements. An index named `...Document` would be taken for a document table. | `SelectShapeTests` |
| 6 | The optimistic concurrency check is written into the `UPDATE` text. | `UpdateDocumentCommand` appends `and [Version] = <n>`, or `and ([Version] IS NULL OR [Version] = <n>)` when the new version is 1, with the number inlined by `GetSqlValue`. It raises `ConcurrencyException` if the count is not 1. | `SqlTree.UpdateCondition` reads exactly these forms. An `UPDATE` whose condition is anything else is refused, so a check can never be skipped. | `SelectShapeTests`, `StatementShapeTests` (every corpus update), `CrudTests` |
| 7 | An `UPDATE` reports whether it changed a row. | `UpdateDocumentCommand` compares the count with 1 for a checked update. | `UpdateAsync` returns 1, or 0 when the row is missing or the version differs. | `StrictSemanticsTests`, the conformance suites |
| 8 | A query over an index is wrapped as `SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [Index] AS a ON a.[DocumentId] = [Document].[Id] WHERE ... GROUP BY [Document].[Id]) AS IndexQuery ON ...`, with the paging inside the derived table and the ordering repeated outside. | `DefaultQuery` and its `QueryState`, in the parts that add the `IndexQuery` join. | `SelectShape` reads the joins, the predicate, the ordering and the paging from either level. | `StatementShapeTests` (every corpus select), the conformance suites |
| 9 | An ordered index query selects each order column as `MAX(a.[Col]) AS order_N` and orders by `order_N`. | `DefaultQuery` when it builds the `GROUP BY` form. | `SelectShape` maps the alias back to the column. An `ORDER BY` it cannot map is refused. | `StatementShapeTests` |
| 10 | A reduce index is joined as `[Index] AS i ON i.[Id] = b.[<Index>Id]`, with `b` the alias of the bridge table. | `DefaultQuery.With<TIndex>` for a `ReduceIndex`. | `SelectShape.Reduce` finds the bridge by that alias. | `StatementShapeTests`, `ReduceQueryOrderTests` |
| 11 | `filterType` adds `[Document].[Type] = @p` to an index query. | `DefaultQuery.For<T>(filterType: true)`. | The term is removed from the index query, because `Type` is not an index column, and applied to the documents afterwards. | `StrictSemanticsTests`, the conformance suites |
| 12 | Counting an index query is `count(distinct [Document].[Id])`; the raw join API counts with `count(1)`. | `DefaultQuery.CountAsync`; `SqlBuilder` in the join tests. | A distinct count counts documents, `count(1)` counts joined rows, and `LEFT` and `RIGHT` are counted exactly. | `StrictSemanticsTests`, `CanRunLeftJoin` and the other join tests of `CoreTests` |
| 13 | `DefaultIdGenerator` seeds itself with `SELECT MAX([Id]) FROM [Document]`. | `DefaultIdGenerator.InitializeCollectionAsync`. | `ScalarRoute.MaxId`. | The conformance suites |
| 14 | `DbBlockIdGenerator` reads and updates one row of `[Identifiers]` with a conditional `UPDATE`. | `DbBlockIdGenerator`: `SELECT [nextval] ... WHERE [dimension] = @dimension`, `UPDATE ... SET [nextval] = @new WHERE [nextval] = @previous AND [dimension] = @dimension`, and the `INSERT` of a new row. | `CosmosDbCommand.Identifiers.cs` answers exactly these three statements; any other form is refused. | `StrictSemanticsTests`, the conformance suites run with `COSMOS_ID_GENERATOR=Block` |
| 15 | A unit of work that is cancelled or fails is released by disposing the transaction, without a rollback. | `Session.CancelAsync` and its handling of a failed read call `DbTransaction.DisposeAsync`. | `CosmosDbTransaction` rolls back when it is disposed and was not committed. | `CancelRollbackTests` |
| 16 | A failure of the store surfaces as a `DbException`. | YesSql 6's `ShouldReleaseTransactionWhen...QueryFails` tests. | `CosmosDbException` (an error from Cosmos) and `SqlSyntaxException` (SQL the parser rejects) both derive from `DbException`. | The YesSql 6 conformance suite, `ErrorHandlingTests` |
| 17 | `SchemaBuilder` hands each command to `ICommandInterpreter`. | `SchemaBuilder`, `SchemaCommand` and the interpreter interface. | Only the rename of a column does anything; see [Schema commands](ARCHITECTURE.md#schema-commands). | `CoreTests.ShouldRenameColumn` |
| 18 | Date functions are produced by the dialect's `Methods` table. | `ISqlDialect.Methods`. | `now`, `year`, `month` and the other date parts are templates that produce `GetCurrentDateTime()` and `DateTimePart(...)`. | The conformance suites |
| 19 | The random order is the dialect's `RandomOrderByClause`. | `IQuery.OrderByRandom`. | Cosmos cannot order by a function, so the clause is a marker the provider recognizes and sorts by itself. | `RandomOrderTests` |

## How a change in YesSql is caught

1. **The corpus.** `test/YesSql.Provider.CosmosDb.Tests/Sql/statements.sql` holds the 300 distinct statements YesSql sent while its own `CoreTests` ran against this provider. Every one is parsed and printed back, and its shape is compared with `Sql/statement-shapes.tsv`. A change in how YesSql writes a statement, or in how the parser reads it, fails there before anything reaches Cosmos.
2. **The conformance suites.** YesSql's `CoreTests` run against the provider on both partition strategies, with both id generators, for YesSql 5.4.7 and for 6.0.0. See [CONFORMANCE.md](CONFORMANCE.md). CI runs them on every pull request.
3. **Refusal.** The provider never skips a part of a statement it does not understand. A `WHERE` term it cannot apply, a join it cannot answer or a statement outside its grammar throws; it does not return a result that ignores the term.

Dependabot is set to ignore major versions of the YesSql packages, so moving to a new YesSql is a deliberate change that runs all of the above.

## What would break

A change that is not covered by a test here is the likeliest one to go unnoticed. The ones to watch for in a new YesSql version:

- A new kind of query that adds a join or a predicate shape the grammar and `SelectShape` do not know. It is refused with an exception that names the statement, which is how it would show up.
- Parameters that are no longer named after their columns (assumption 1), or batching enabled by default (assumption 2).
- A version check that is bound as a parameter instead of written into the text (assumption 6). The provider would refuse such an update.
- A different way to learn the id of a new index row (assumption 3).
- Tables whose names do not follow the convention (assumption 5), for example a collection name that makes a map index end in `Document`.
