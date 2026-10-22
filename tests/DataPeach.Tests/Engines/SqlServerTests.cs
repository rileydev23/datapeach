using DataPeach.Core;

namespace DataPeach.Tests.Engines;

public sealed class SqlServerTests : EngineTests
{
    // The container's certificate is self-signed.
    protected override ConnectionEntry Entry => new()
    {
        Kind = DbKind.SqlServer, Host = "localhost", Port = 51433, Database = "master", User = "sa", Options = "TrustServerCertificate=True",
    };
    protected override string Password => "Peach_12345";
    protected override string? Schema => "dbo";
    protected override string? SystemDatabase => "tempdb";
    protected override string SlowQuery => "WAITFOR DELAY '00:00:10'";

    protected override string Setup => """
        DROP VIEW IF EXISTS v_users;
        DROP TABLE IF EXISTS orders, log, users;
        CREATE TABLE users (id INT PRIMARY KEY, name NVARCHAR(100) NOT NULL, active INT DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id INT PRIMARY KEY, user_id INT REFERENCES users(id), total DECIMAL(10,2));
        CREATE TABLE log (msg NVARCHAR(MAX));
        GO
        CREATE VIEW v_users AS SELECT name FROM users
        GO
        INSERT INTO users (id, name)
        SELECT i, CONCAT('user ', i) FROM (
            SELECT a.n * 1000 + b.n * 100 + c.n * 10 + e.n + 1 AS i
            FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n),
                 (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) e(n)) x
        WHERE i <= 3000;
        """;

    protected override string? RoutineSetup => """
        CREATE OR ALTER FUNCTION dbo.add_one(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END
        GO
        CREATE OR ALTER PROCEDURE dbo.touch_user @id INT, @n INT OUTPUT AS
        BEGIN
            UPDATE users SET active = 0 WHERE id = @id;
            SET @n = @@ROWCOUNT;
        END
        GO
        """;
}
