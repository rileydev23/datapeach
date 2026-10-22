using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Sql;

namespace DataPeach.Tests;

public class RoutineTests
{
    private static DbRoutine Proc(string? schema, params DbParameter[] ps) =>
        new() { Schema = schema, Name = "give_bonus", Kind = DbRoutineKind.Procedure, Parameters = ps.ToList() };

    [Fact]
    public void TypedValuesBecomeLiterals()
    {
        var d = PostgresDialect.Instance;
        Assert.Equal("NULL", d.ArgLiteral("", new DbParameter("a", "integer", ParamDirection.In)));
        Assert.Equal("42", d.ArgLiteral("42", new DbParameter("a", "integer", ParamDirection.In)));
        Assert.Equal("'42'", d.ArgLiteral("42", new DbParameter("a", "text", ParamDirection.In)));
        Assert.Equal("'O''Neil'", d.ArgLiteral("O'Neil", new DbParameter("a", "varchar", ParamDirection.In)));
        Assert.Equal("TRUE", d.ArgLiteral("true", new DbParameter("a", "boolean", ParamDirection.In)));
        Assert.Equal("CURRENT_DATE", d.ArgLiteral("=CURRENT_DATE", new DbParameter("a", "date", ParamDirection.In)));
    }

    [Fact]
    public void PostgresCallsProceduresAndSelectsFunctions()
    {
        var p = Proc("public", new DbParameter("id", "integer", ParamDirection.In), new DbParameter("total", "numeric", ParamDirection.Out));
        Assert.Equal("CALL give_bonus(5, NULL)", PostgresDialect.Instance.CallSql(p, new[] { "5" }));
        Assert.Equal("DROP PROCEDURE give_bonus(integer, numeric)", PostgresDialect.Instance.DropSql(p));
        var f = new DbRoutine { Schema = "sales", Name = "total", Kind = DbRoutineKind.Function, Parameters = { new DbParameter("y", "integer", ParamDirection.In) } };
        Assert.Equal("SELECT * FROM sales.total(2024)", PostgresDialect.Instance.CallSql(f, new[] { "2024" }));
        Assert.Equal("DROP FUNCTION sales.total(integer)", PostgresDialect.Instance.DropSql(f));
    }

    [Fact]
    public void MySqlReadsOutParametersFromSessionVariables()
    {
        var p = Proc("shop", new DbParameter("id", "int", ParamDirection.In), new DbParameter("n", "int", ParamDirection.InOut), new DbParameter("total", "decimal(10,2)", ParamDirection.Out));
        Assert.Equal("SET @n = 3;\nCALL shop.give_bonus(1, @n, @total);\nSELECT @n AS n, @total AS total", MySqlDialect.Instance.CallSql(p, new[] { "1", "3" }));
    }

    [Theory]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Sybase)]
    public void TsqlDeclaresOutputParameters(DbKind kind)
    {
        var p = Proc("dbo", new DbParameter("@id", "int", ParamDirection.In), new DbParameter("@total", "money", ParamDirection.InOut));
        Assert.Equal("DECLARE @total money\nSELECT @total = 10\nEXEC give_bonus @id = 1, @total = @total OUTPUT\nSELECT @total AS total",
            Dialect.For(kind).CallSql(p, new[] { "1", "10" }));
    }

    [Fact]
    public void SybaseFunctionsAreCalledWithTheirOwner()
    {
        var f = new DbRoutine { Schema = "dbo", Name = "f_add", Kind = DbRoutineKind.Function, Parameters = { new DbParameter("@x", "int", ParamDirection.In) } };
        Assert.Equal("SELECT dbo.f_add(1)", SybaseDialect.Instance.CallSql(f, new[] { "1" }));
    }

    [Fact]
    public void OracleWrapsOutParametersInABlock()
    {
        var p = Proc("HR", new DbParameter("P_ID", "NUMBER", ParamDirection.In), new DbParameter("P_NAME", "VARCHAR2", ParamDirection.Out));
        Assert.Equal("DECLARE\n  v_p_name VARCHAR2(4000);\nBEGIN\n  HR.GIVE_BONUS(1, v_p_name);\nEND;\n/".Replace("HR.GIVE_BONUS", "HR.\"give_bonus\""),
            OracleDialect.Instance.CallSql(p, new[] { "1" }));
        var f = new DbRoutine { Schema = "HR", Name = "TOTAL", Kind = DbRoutineKind.Function, Parameters = { new DbParameter("Y", "NUMBER", ParamDirection.In) } };
        Assert.Equal("SELECT HR.TOTAL(2024) FROM dual", OracleDialect.Instance.CallSql(f, new[] { "2024" }));
    }

    [Fact]
    public void SqlServerCodeBecomesCreateOrAlter()
    {
        Assert.Equal("-- note\nCREATE OR ALTER PROCEDURE p AS SELECT 1", SqlServerDialect.CreateOrAlter("-- note\nCREATE PROCEDURE p AS SELECT 1", DbKind.SqlServer));
        Assert.Equal("CREATE OR ALTER VIEW v AS SELECT 1", SqlServerDialect.CreateOrAlter("CREATE OR ALTER VIEW v AS SELECT 1", DbKind.SqlServer));
    }

    [Fact]
    public void SignatureTellsOverloadsApart()
    {
        var f = new DbRoutine { Name = "f", Kind = DbRoutineKind.Function, Parameters = { new DbParameter("a", "integer", ParamDirection.In), new DbParameter("b", "text", ParamDirection.InOut) } };
        Assert.Equal("(a integer, INOUT b text)", f.Signature);
    }
}

public class ScriptSplitTests
{
    [Fact]
    public void MySqlDelimiterLinesChangeTheSeparator()
    {
        const string sql = "DROP PROCEDURE IF EXISTS p;\n\nDELIMITER $$\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND$$\nDELIMITER ;\nCALL p();";
        var parts = SqlStatements.Split(sql, DbKind.MySql).Select(s => s.Text).ToList();
        Assert.Equal(new[] { "DROP PROCEDURE IF EXISTS p", "CREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND", "CALL p()" }, parts);
    }

    [Fact]
    public void SybaseSplitsAtGoAndKeepsProcedureBodies()
    {
        const string sql = "create procedure p as\nbegin\n  select 1;\n  select 2;\nend\ngo\nexec p";
        var parts = SqlStatements.Split(sql, DbKind.Sybase).Select(s => s.Text).ToList();
        Assert.Equal(new[] { "create procedure p as\nbegin\n  select 1;\n  select 2;\nend", "exec p" }, parts);
    }

    [Fact]
    public void DollarsAreNotStringsInMySql()
    {
        var tokens = SqlTokenizer.Tokenize("SELECT $$x$$", DbKind.MySql);
        Assert.DoesNotContain(tokens, t => t.Kind == SqlTokenKind.String);
        Assert.Contains(SqlTokenizer.Tokenize("SELECT $$x$$", DbKind.Postgres), t => t.Kind == SqlTokenKind.String);
    }
}
