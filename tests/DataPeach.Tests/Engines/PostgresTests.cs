using DataPeach.Core;

namespace DataPeach.Tests.Engines;

public class PostgresTests : EngineTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Postgres, Host = "localhost", Port = 55432, Database = "peach", User = "peach" };
    protected override string? Schema => "public";
    protected override string SlowQuery => "SELECT pg_sleep(10)";

    protected override string Setup => """
        DROP VIEW IF EXISTS v_users;
        DROP TABLE IF EXISTS orders, log, users;
        CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, active INT DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id INT PRIMARY KEY, user_id INT REFERENCES users(id), total NUMERIC(10,2));
        CREATE TABLE log (msg TEXT);
        CREATE VIEW v_users AS SELECT name FROM users;
        INSERT INTO users (id, name) SELECT i, 'user ' || i FROM generate_series(1, 3000) i;
        """;
}

/// <summary>CockroachDB speaks the PostgreSQL protocol and catalog: same driver and dialect.</summary>
public sealed class CockroachTests : PostgresTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Postgres, Host = "localhost", Port = 56257, Database = "defaultdb", User = "root" };
    protected override string Password => "";
}
