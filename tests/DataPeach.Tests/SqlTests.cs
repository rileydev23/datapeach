using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Sql;

namespace DataPeach.Tests;

public class TokenizerTests
{
    private static List<(SqlTokenKind, string)> Tok(string sql, DbKind kind = DbKind.Postgres) =>
        SqlTokenizer.Tokenize(sql, kind).Where(t => t.Kind != SqlTokenKind.Whitespace).Select(t => (t.Kind, t.Text(sql))).ToList();

    [Fact]
    public void ReadsTheUsualPieces()
    {
        var t = Tok("SELECT a.\"Name\", 'it''s' -- hi\nFROM t WHERE x >= 1.5e3 AND y = $1");
        Assert.Equal((SqlTokenKind.Keyword, "SELECT"), t[0]);
        Assert.Contains((SqlTokenKind.QuotedIdentifier, "\"Name\""), t);
        Assert.Contains((SqlTokenKind.String, "'it''s'"), t);
        Assert.Contains((SqlTokenKind.Comment, "-- hi"), t);
        Assert.Contains((SqlTokenKind.Number, "1.5e3"), t);
        Assert.Contains((SqlTokenKind.Operator, ">="), t);
        Assert.Contains((SqlTokenKind.Parameter, "$1"), t);
    }

    [Fact]
    public void DollarQuotingAndNestedComments()
    {
        var t = Tok("SELECT $fn$ a; 'b' $fn$, /* x /* y */ z */ 1");
        Assert.Contains((SqlTokenKind.String, "$fn$ a; 'b' $fn$"), t);
        Assert.Contains((SqlTokenKind.Comment, "/* x /* y */ z */"), t);
    }

    [Fact]
    public void HashIsACommentOnlyInMySql()
    {
        Assert.Contains(Tok("SELECT 1 # c", DbKind.MySql), x => x.Item1 == SqlTokenKind.Comment);
        Assert.DoesNotContain(Tok("SELECT d #>> '{a}'"), x => x.Item1 == SqlTokenKind.Comment);
    }

    [Fact]
    public void UnclosedStringsRunToTheEndWithoutThrowing()
    {
        var t = Tok("SELECT 'abc");
        Assert.Equal((SqlTokenKind.String, "'abc"), t[^1]);
    }
}

public class StatementTests
{
    [Fact]
    public void SplitsOnSemicolonsOutsideStringsAndComments()
    {
        var all = SqlStatements.Split("select ';'; -- a; b\nselect 2;\n\n;  ");
        Assert.Equal(new[] { "select ';'", "select 2" }, all.Select(s => s.Text));
    }

    [Fact]
    public void SqlServerAlsoSplitsOnGoLines()
    {
        var sql = "create view v as select 1 as go\r\nGO\r\n  go  \nselect 'GO'\ngo";
        Assert.Equal(new[] { "create view v as select 1 as go", "select 'GO'" }, SqlStatements.Split(sql, DbKind.SqlServer).Select(s => s.Text));
        // Elsewhere GO is just a word.
        Assert.Single(SqlStatements.Split(sql, DbKind.Postgres));
    }

    [Fact]
    public void ProceduresKeepTheirSemicolonsUntilTheSeparatorLine()
    {
        var tsql = "create or alter procedure p as begin select 1; select 2; end\nGO\nbegin tran; select 3;";
        Assert.Equal(new[] { "create or alter procedure p as begin select 1; select 2; end", "begin tran", "select 3" },
            SqlStatements.Split(tsql, DbKind.SqlServer).Select(s => s.Text));

        var plsql = "CREATE OR REPLACE FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;\n/\nselect 1 from dual;\nBEGIN\n  null;\nEND;\n/\nselect 1/2 from dual";
        Assert.Equal(new[] { "CREATE OR REPLACE FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;", "select 1 from dual", "BEGIN\n  null;\nEND;", "select 1/2 from dual" },
            SqlStatements.Split(plsql, DbKind.Oracle).Select(s => s.Text));
    }

    [Theory]
    [InlineData("select 1;\nselect 2;", 3, "select 1")]
    [InlineData("select 1;\nselect 2;", 9, "select 1")]   // right after the ';'
    [InlineData("select 1;\nselect 2;", 12, "select 2")]
    [InlineData("select 1;\n\n\nselect 2;", 11, "select 2")] // blank line in between: the next one
    [InlineData("select 1;\nselect 2;", 19, "select 2")]  // end of the text
    public void PicksTheStatementUnderTheCaret(string sql, int caret, string expected) =>
        Assert.Equal(expected, SqlStatements.At(sql, caret)!.Value.Text);

    [Theory]
    [InlineData("select * from t", false)]
    [InlineData("  -- c\n SELECT 1", false)]
    [InlineData("with x as (select 1) select * from x", false)]
    [InlineData("with x as (delete from t returning *) select * from x", true)]
    [InlineData("update t set a = 1", true)]
    [InlineData("select replace(a, 'x', 'y') from t", false)]
    [InlineData("select * into copy from t", true)]
    [InlineData("pragma table_info(t)", false)]
    [InlineData("pragma journal_mode = wal", true)]
    [InlineData("explain select 1", false)]
    [InlineData("vacuum", true)]
    [InlineData("set search_path = x", true)]
    public void KnowsWhichStatementsWrite(string sql, bool writes) => Assert.Equal(writes, SqlStatements.IsWrite(sql));
}

public class CompletionTests
{
    private static readonly SchemaSnapshot Schema = Build();

    private static SchemaSnapshot Build()
    {
        var users = new DbTable { Schema = "public", Name = "users" };
        users.Columns.Add(new DbColumn("id", "integer", false, true, null));
        users.Columns.Add(new DbColumn("email", "text", false, false, null));
        var orders = new DbTable { Schema = "public", Name = "orders" };
        orders.Columns.Add(new DbColumn("id", "integer", false, true, null));
        orders.Columns.Add(new DbColumn("user_id", "integer", false, false, null));
        orders.Columns.Add(new DbColumn("Total", "numeric", true, false, null));
        var audit = new DbTable { Schema = "audit", Name = "log" };
        return new SchemaSnapshot { Schemas = { "public", "audit" }, Tables = { users, orders, audit }, DefaultSchema = "public" };
    }

    private static List<string> Labels(string sqlWithCaret)
    {
        int caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);
        return SqlCompletion.Suggest(sql, caret, Schema, PostgresDialect.Instance).Items.Select(i => i.Label).ToList();
    }

    [Fact]
    public void TablesAfterFrom() => Assert.Equal(new[] { "orders" }, Labels("select * from or|").Take(1));

    [Fact]
    public void ColumnsOfAnAlias() =>
        Assert.Equal(new[] { "id", "user_id", "Total" }, Labels("select o.| from orders o join users u on u.id = o.user_id"));

    [Fact]
    public void ColumnsOfTheTablesInTheStatementComeFirst()
    {
        var l = Labels("select em| from users");
        Assert.Equal("email", l[0]);
    }

    [Fact]
    public void TablesOfASchema() => Assert.Equal(new[] { "log" }, Labels("select * from audit.|"));

    [Fact]
    public void MixedCaseNamesAreQuotedForPostgres()
    {
        var sql = "select o.T from orders o";
        var r = SqlCompletion.Suggest(sql, sql.IndexOf(" from", StringComparison.Ordinal), Schema, PostgresDialect.Instance);
        Assert.Equal("\"Total\"", r.Items[0].Insert);
        Assert.Equal(1, r.Length);
    }
}
