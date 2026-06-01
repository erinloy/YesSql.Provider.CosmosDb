using System.Collections.Generic;
using System.Text;
using YesSql.Sql;
using YesSql.Sql.Schema;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Schema-command interpreter for Cosmos DB. Cosmos is schemaless, so all DDL (create/alter/drop table,
/// columns, indexes, foreign keys) is a no-op: the single container is provisioned by the connection,
/// and index properties live embedded in the document served by Cosmos automatic indexing.
/// </summary>
public sealed class CosmosDbCommandInterpreter : ICommandInterpreter
{
    private static readonly string[] None = System.Array.Empty<string>();

    public IEnumerable<string> CreateSql(IEnumerable<ISchemaCommand> commands) => None;
    public IEnumerable<string> Run(ICreateTableCommand command) => None;
    public IEnumerable<string> Run(IDropTableCommand command) => None;
    public IEnumerable<string> Run(IAlterTableCommand command) => None;
    public void Run(StringBuilder builder, IAddColumnCommand command) { }
    public void Run(StringBuilder builder, IDropColumnCommand command) { }
    public void Run(StringBuilder builder, IAlterColumnCommand command) { }
    public void Run(StringBuilder builder, IAddIndexCommand command) { }
    public void Run(StringBuilder builder, IDropIndexCommand command) { }
    public IEnumerable<string> Run(ISqlStatementCommand command) => None;
    public IEnumerable<string> Run(ICreateForeignKeyCommand command) => None;
    public IEnumerable<string> Run(IDropForeignKeyCommand command) => None;
}
