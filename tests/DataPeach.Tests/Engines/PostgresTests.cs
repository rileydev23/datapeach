using DataPeach.Core;

namespace DataPeach.Tests.Engines;

public class PostgresTests : EngineTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Postgres, Host = "localhost", Port = 55432, Database = "peach", User = "peach" };
    protected override string? Schema => "public";
    protected override string? SystemDatabase => "template1";
    protected override string SlowQuery => "SELECT pg_sleep(10)";

    protected override string Setup => """
        DROP PROCEDURE IF EXISTS touch_user;
        DROP VIEW IF EXISTS v_users;
        DROP TABLE IF EXISTS orders, log, users;
        CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, active INT DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id INT PRIMARY KEY, user_id INT REFERENCES users(id), total NUMERIC(10,2));
        CREATE TABLE log (msg TEXT);
        CREATE VIEW v_users AS SELECT name FROM users;
        INSERT INTO users (id, name) SELECT i, 'user ' || i FROM generate_series(1, 3000) i;
        """;

    protected override string? RoutineSetup => """
        CREATE OR REPLACE FUNCTION add_one(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x + 1 $$;
        CREATE OR REPLACE PROCEDURE touch_user(p_id integer) LANGUAGE sql AS $$ UPDATE users SET active = 0 WHERE id = p_id $$;
        """;
}

/// <summary>CockroachDB speaks the PostgreSQL protocol and catalog: same driver and dialect.</summary>
public sealed class CockroachTests : PostgresTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Postgres, Host = "localhost", Port = 56257, Database = "defaultdb", User = "root" };
    protected override string Password => "";
    protected override string? SystemDatabase => "system";

    // Its SQL procedures only take $1-style parameters, and it then fails to run their code back: the function only.
    protected override string? RoutineSetup => "CREATE OR REPLACE FUNCTION add_one(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x + 1 $$;";
}
