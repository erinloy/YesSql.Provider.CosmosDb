using YesSql.Sql.Schema;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// What the interpreter does with the schema commands YesSql sends: nothing for the changes a container has no schema for, a
/// rewrite for a renamed column, and a refusal for raw SQL, which has nothing to run against.
/// </summary>
public class CommandInterpreterTests
{
    private static readonly CosmosDbCommandInterpreter Interpreter = new();

    [Fact]
    public void Raw_SQL_in_a_migration_is_refused()
    {
        var thrown = Assert.Throws<NotSupportedException>(() => Interpreter.CreateSql([new SqlStatementCommand("UPDATE [T] SET [A] = 1")]).ToList());
        Assert.Contains("UPDATE [T] SET [A] = 1", thrown.Message);
    }

    [Fact]
    public void Raw_SQL_tagged_for_particular_providers_is_skipped_as_YesSql_skips_it()
    {
        var tagged = new SqlStatementCommand("SELECT 1").ForProvider("SqlServer");
        Assert.Empty(Interpreter.CreateSql([tagged]));
    }

    [Fact]
    public void Changes_to_tables_and_columns_do_nothing()
    {
        var statements = Interpreter.CreateSql([new CreateTableCommand("T"), new DropTableCommand("T")]);
        Assert.Empty(statements);
    }

    [Fact]
    public void A_renamed_column_becomes_a_rewrite_of_the_field()
    {
        var alter = new AlterTableCommand("T", new CosmosDbDialect(), "");
        alter.RenameColumn("Old", "New");
        Assert.Equal(["renamecolumn [T] [Old] [New]"], Interpreter.CreateSql([alter]));
    }

    [Fact]
    public void Changes_around_a_rename_are_still_no_ops()
    {
        var alter = new AlterTableCommand("T", new CosmosDbDialect(), "");
        alter.AddColumn<string>("Extra");
        alter.RenameColumn("Old", "New");
        alter.DropColumn("Gone");
        Assert.Equal(["renamecolumn [T] [Old] [New]"], Interpreter.CreateSql([alter]));
    }
}
