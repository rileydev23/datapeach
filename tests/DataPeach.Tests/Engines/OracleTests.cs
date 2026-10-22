using DataPeach.Core;

namespace DataPeach.Tests.Engines;

public sealed class OracleTests : EngineTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.Oracle, Host = "localhost", Port = 51521, Database = "FREEPDB1", User = "peach" };
    protected override string? Schema => "PEACH";
    // DBMS_SESSION.SLEEP cannot be interrupted by a cancel; a heavy query can.
    protected override string SlowQuery => "SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects c";

    protected override string Setup => """
        DROP VIEW IF EXISTS v_users;
        DROP TABLE IF EXISTS orders;
        DROP TABLE IF EXISTS log;
        DROP TABLE IF EXISTS users;
        CREATE TABLE users (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100) NOT NULL, active NUMBER(1) DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id NUMBER(10) PRIMARY KEY, user_id NUMBER(10) REFERENCES users(id), total NUMBER(10,2));
        CREATE TABLE log (msg CLOB);
        CREATE VIEW v_users AS SELECT name FROM users;
        INSERT INTO users (id, name) SELECT LEVEL, 'user ' || LEVEL FROM dual CONNECT BY LEVEL <= 3000;
        COMMIT;
        """;

    protected override string? RoutineSetup => """
        CREATE OR REPLACE FUNCTION add_one(x NUMBER) RETURN NUMBER IS
        BEGIN
            RETURN x + 1;
        END;
        /
        CREATE OR REPLACE PROCEDURE touch_user(id IN NUMBER, n OUT NUMBER) IS
        BEGIN
            UPDATE users SET active = 0 WHERE users.id = touch_user.id;
            n := SQL%ROWCOUNT;
        END;
        /
        """;
}
