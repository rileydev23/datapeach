using DataPeach.Core;

namespace DataPeach.Tests.Engines;

/// <summary>SAP ASE 16 (Sybase) from the datagrip/sybase image: its own login and database.</summary>
public sealed class SybaseTests : EngineTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Sybase, Host = "localhost", Port = 55000, Database = "testdb", User = "sa" };
    protected override string Password => "myPassword";
    protected override string? Schema => "dbo";
    protected override string SlowQuery => "WAITFOR DELAY '00:00:10'";

    // Columns are NOT NULL unless they say NULL; there is no VALUES list of rows, so the users come from a loop.
    protected override string Setup => """
        IF OBJECT_ID('v_users') IS NOT NULL DROP VIEW v_users
        GO
        IF OBJECT_ID('orders') IS NOT NULL DROP TABLE orders
        GO
        IF OBJECT_ID('log') IS NOT NULL DROP TABLE log
        GO
        IF OBJECT_ID('users') IS NOT NULL DROP TABLE users
        GO
        CREATE TABLE users (id INT NOT NULL PRIMARY KEY, name VARCHAR(100) NOT NULL, active INT DEFAULT 1 NULL)
        GO
        CREATE INDEX ix_users_name ON users(name)
        GO
        CREATE TABLE orders (id INT NOT NULL PRIMARY KEY, user_id INT NULL REFERENCES users(id), total NUMERIC(10,2) NULL)
        GO
        CREATE TABLE log (msg TEXT NULL)
        GO
        CREATE VIEW v_users AS SELECT name FROM users
        GO
        DECLARE @i INT
        SELECT @i = 1
        WHILE @i <= 3000
        BEGIN
            INSERT INTO users (id, name) VALUES (@i, 'user ' + CONVERT(VARCHAR(10), @i))
            SELECT @i = @i + 1
        END
        GO
        """;

    protected override string? RoutineSetup => """
        IF OBJECT_ID('add_one') IS NOT NULL DROP FUNCTION add_one
        GO
        CREATE FUNCTION add_one(@x INT) RETURNS INT AS RETURN @x + 1
        GO
        IF OBJECT_ID('touch_user') IS NOT NULL DROP PROCEDURE touch_user
        GO
        CREATE PROCEDURE touch_user @id INT, @n INT OUTPUT AS
        BEGIN
            UPDATE users SET active = 0 WHERE id = @id
            SELECT @n = @@rowcount
        END
        GO
        """;
}
