using System.Collections.Generic;
using System.Linq;
using System.Text;
using YesSql.Sql;
using YesSql.Sql.Schema;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Schema-command interpreter for Cosmos DB. Cosmos is schemaless, so most DDL (create/alter/drop table,
/// add/drop columns, indexes, foreign keys) is a no-op: the single container is provisioned by the
/// connection, and index properties live embedded in the document served by Cosmos automatic indexing.
/// The exception is RenameColumn, which must rewrite the field on every stored row — it is emitted as a
/// "renamecolumn [table] [old] [new]" statement that the command executes against the partition.
/// </summary>
public sealed class CosmosDbCommandInterpreter : ICommandInterpreter
{
    private static readonly string[] None = System.Array.Empty<string>();

    public IEnumerable<string> CreateSql(IEnumerable<ISchemaCommand> commands)
        => commands.OfType<IAlterTableCommand>().SelectMany(Run).ToList();

    public IEnumerable<string> Run(ICreateTableCommand command) => None;
    public IEnumerable<string> Run(IDropTableCommand command) => None;

    public IEnumerable<string> Run(IAlterTableCommand command)
        => command.TableCommands.OfType<RenameColumnCommand>()
            .Select(rename => $"renamecolumn [{command.Name}] [{rename.ColumnName}] [{rename.NewColumnName}]")
            .ToList();

    public void Run(StringBuilder builder, IAddColumnCommand command) { }
    public void Run(StringBuilder builder, IDropColumnCommand command) { }
    public void Run(StringBuilder builder, IAlterColumnCommand command) { }
    public void Run(StringBuilder builder, IAddIndexCommand command) { }
    public void Run(StringBuilder builder, IDropIndexCommand command) { }
    public IEnumerable<string> Run(ISqlStatementCommand command) => None;
    public IEnumerable<string> Run(ICreateForeignKeyCommand command) => None;
    public IEnumerable<string> Run(IDropForeignKeyCommand command) => None;
}
