using DataPeach.Core;

namespace DataPeach.Tests.Engines;

public class MySqlTests : EngineTests
{
    protected override ConnectionEntry Entry => new() { Kind = DbKind.MySql, Host = "localhost", Port = Port, Database = "peach", User = "peach" };
    protected virtual int Port => 53306;
    protected override string? Schema => "peach";
    protected override string SlowQuery => "SELECT SLEEP(10)";

    protected override string Setup => """
        DROP VIEW IF EXISTS v_users;
        DROP TABLE IF EXISTS orders, log, users;
        CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, active INT DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id INT PRIMARY KEY, user_id INT, total DECIMAL(10,2), FOREIGN KEY (user_id) REFERENCES users(id));
        CREATE TABLE log (msg TEXT);
        CREATE VIEW v_users AS SELECT name FROM users;
        INSERT INTO users (id, name)
        WITH d(n) AS (SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                      UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)
        SELECT i, CONCAT('user ', i) FROM (SELECT a.n * 1000 + b.n * 100 + c.n * 10 + e.n + 1 AS i FROM d a, d b, d c, d e) x WHERE i <= 3000;
        """;

    // The compose file sets log_bin_trust_function_creators, so a user without SUPER may create functions.
    protected override string? RoutineSetup => """
        DROP FUNCTION IF EXISTS add_one;
        CREATE FUNCTION add_one(x INT) RETURNS INT DETERMINISTIC RETURN x + 1;
        DROP PROCEDURE IF EXISTS touch_user;
        DELIMITER $$
        CREATE PROCEDURE touch_user(IN id INT, OUT n INT)
        BEGIN
            UPDATE users SET active = 0 WHERE users.id = id;
            SET n = ROW_COUNT();
        END$$
        DELIMITER ;
        """;
}

/// <summary>MariaDB speaks the MySQL protocol: same driver and dialect.</summary>
public sealed class MariaDbTests : MySqlTests
{
    protected override int Port => 53307;
}
