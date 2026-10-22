using System.Data.Common;

namespace DataPeach.Core.Db;

/// <summary>
/// A common connection string option shown as a checkbox under "Advanced": checked means <see cref="Key"/> is set
/// to <see cref="Value"/>. <see cref="Id"/> names its label in <see cref="Strings"/> ("conn.flag.&lt;id&gt;").
/// </summary>
public sealed record ConnectionFlag(string Id, string Key, string Value, params string[] Aliases)
{
    public bool Matches(string key) =>
        key.Equals(Key, StringComparison.OrdinalIgnoreCase) || Aliases.Any(a => key.Equals(a, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The checkboxes of each engine and the round trip between them and <see cref="ConnectionEntry.Options"/>: the
/// options string stays the single place they are saved, so older settings files keep working and a flag typed by
/// hand shows up checked.
/// </summary>
public static class ConnectionFlags
{
    public static ConnectionFlag[] For(DbKind kind) => kind switch
    {
        DbKind.SqlServer => new ConnectionFlag[]
        {
            // "The certificate chain was issued by an authority that is not trusted": a self-signed server certificate.
            new("trustCert", "TrustServerCertificate", "True", "Trust Server Certificate"),
            new("noEncrypt", "Encrypt", "False"),
            new("readIntent", "ApplicationIntent", "ReadOnly", "Application Intent"),
            new("multiSubnet", "MultiSubnetFailover", "True", "Multi Subnet Failover"),
        },
        DbKind.Postgres => new ConnectionFlag[]
        {
            new("sslRequire", "SSL Mode", "Require", "SslMode"),
            new("errorDetail", "Include Error Detail", "True", "IncludeErrorDetail"),
        },
        DbKind.MySql => new ConnectionFlag[]
        {
            // "Public Key Retrieval is not allowed": MySQL 8 accounts without SSL.
            new("publicKey", "AllowPublicKeyRetrieval", "True", "Allow Public Key Retrieval"),
            new("noSsl", "SslMode", "None", "SSL Mode", "Ssl-Mode"),
            new("zeroDate", "ConvertZeroDateTime", "True", "Convert Zero DateTime", "Convert Zero Datetime"),
        },
        DbKind.Oracle => new ConnectionFlag[]
        {
            new("sysdba", "DBA Privilege", "SYSDBA"),
        },
        DbKind.Sybase => new ConnectionFlag[]
        {
            // Servers with "net password encryption reqd" refuse a plain-text login.
            new("encryptPwd", "EncryptPassword", "1"),
        },
        DbKind.Sqlite => new ConnectionFlag[]
        {
            new("foreignKeys", "Foreign Keys", "True", "ForeignKeys"),
        },
        _ => Array.Empty<ConnectionFlag>(),
    };

    /// <summary>
    /// Splits <paramref name="options"/> into the flags of <paramref name="kind"/> it sets and the rest, which goes
    /// back to the free text box. A malformed string is left whole as the rest: the driver reports it better.
    /// </summary>
    public static (HashSet<string> Checked, string Others) Split(DbKind kind, string options)
    {
        var on = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(options)) return (on, "");
        DbConnectionStringBuilder all;
        try { all = new DbConnectionStringBuilder { ConnectionString = options }; }
        catch { return (on, options.Trim()); }
        var rest = new DbConnectionStringBuilder();
        foreach (string key in all.Keys)
        {
            var value = Convert.ToString(all[key]) ?? "";
            var flag = For(kind).FirstOrDefault(f => f.Matches(key));
            if (flag is not null && SameValue(value, flag.Value)) on.Add(flag.Id);
            else rest[key] = all[key];
        }
        return (on, rest.ConnectionString);
    }

    /// <summary>
    /// The options string for <paramref name="rest"/> plus the checked flags. A flag also set in the text wins over
    /// what the text says; unchecked flags add nothing (the driver default).
    /// </summary>
    public static string Merge(DbKind kind, IEnumerable<string> checkedIds, string rest)
    {
        var ids = checkedIds.ToHashSet();
        var flags = For(kind).Where(f => ids.Contains(f.Id)).ToList();
        if (flags.Count == 0) return rest.Trim();
        var b = new DbConnectionStringBuilder();
        try { if (!string.IsNullOrWhiteSpace(rest)) b.ConnectionString = rest; }
        catch { return string.Join(";", flags.Select(f => f.Key + "=" + f.Value).Prepend(rest.Trim().TrimEnd(';'))); }
        foreach (var f in flags)
        {
            foreach (var k in b.Keys.Cast<string>().Where(f.Matches).ToList()) b.Remove(k);
            b[f.Key] = f.Value;
        }
        return b.ConnectionString;
    }

    private static bool SameValue(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || (IsTrue(a) && IsTrue(b));

    private static bool IsTrue(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1";
}
