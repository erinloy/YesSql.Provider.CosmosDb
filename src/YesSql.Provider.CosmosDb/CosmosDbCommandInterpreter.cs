using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YesSql.Sql;
using YesSql.Sql.Schema;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Schema-command interpreter for Cosmos DB. A container has no schema, so creating, altering and dropping tables, columns,
/// indexes and foreign keys do nothing. Raw SQL in a migration cannot run, so it throws. The container is provisioned by the
/// connection, and Cosmos indexes every property. The exception is RenameColumn, which has to rewrite the field in every item
/// of the table: it is emitted as "renamecolumn [table] [old] [new]", which the command executes.
/// </summary>
public sealed class CosmosDbCommandInterpreter : ICommandInterpreter
{
    private static readonly string[] None = Array.Empty<string>();

    /// <inheritdoc />
    public IEnumerable<string> CreateSql(IEnumerable<ISchemaCommand> commands)
    {
        var statements = new List<string>();
        foreach (var command in commands)
        {
            switch (command)
            {
                case ISqlStatementCommand raw:
                    statements.AddRange(Run(raw));
                    break;
                case IAlterTableCommand alter:
                    statements.AddRange(Run(alter));
                    break;
            }
        }

        return statements;
    }

    /// <inheritdoc />
    public IEnumerable<string> Run(ICreateTableCommand command) => None;
    /// <inheritdoc />
    public IEnumerable<string> Run(IDropTableCommand command) => None;

    /// <inheritdoc />
    public IEnumerable<string> Run(IAlterTableCommand command)
        => command.TableCommands.OfType<RenameColumnCommand>()
            .Select(rename => $"renamecolumn [{command.Name}] [{rename.ColumnName}] [{rename.NewColumnName}]")
            .ToList();

    /// <inheritdoc />
    public void Run(StringBuilder builder, IAddColumnCommand command) { }
    /// <inheritdoc />
    public void Run(StringBuilder builder, IDropColumnCommand command) { }
    /// <inheritdoc />
    public void Run(StringBuilder builder, IAlterColumnCommand command) { }
    /// <inheritdoc />
    public void Run(StringBuilder builder, IAddIndexCommand command) { }
    /// <inheritdoc />
    public void Run(StringBuilder builder, IDropIndexCommand command) { }
    /// <summary>
    /// Raw SQL in a migration cannot run: there is no SQL engine behind the container. The relational providers run the statement,
    /// or fail if it is invalid, so this fails and does not skip it. A statement tagged for particular providers is skipped, as the
    /// base interpreter of YesSql does.
    /// </summary>
    /// <exception cref="NotSupportedException">The statement is not tagged for particular providers.</exception>
    public IEnumerable<string> Run(ISqlStatementCommand command)
        => command.Providers.Count != 0
            ? None
            : throw new NotSupportedException($"Cosmos DB cannot run the raw SQL statement of a migration: {command.Sql}");
    /// <inheritdoc />
    public IEnumerable<string> Run(ICreateForeignKeyCommand command) => None;
    /// <inheritdoc />
    public IEnumerable<string> Run(IDropForeignKeyCommand command) => None;
}
