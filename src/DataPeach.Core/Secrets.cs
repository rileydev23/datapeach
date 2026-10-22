using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataPeach.Core;

/// <summary>
/// Passwords, by connection id, in <c>secrets.json</c> next to the settings. On Windows each one is encrypted with
/// DPAPI for the current user, so the file is useless on another account or machine. Elsewhere the file is only
/// readable by its owner (chmod 600). They never go into <c>state.json</c> or a project folder.
/// </summary>
public sealed class Secrets
{
    private readonly string _file;
    private readonly object _lock = new();
    private static readonly byte[] Entropy = "DataPeach.secrets.v1"u8.ToArray();

    public Secrets(string folder)
    {
        _file = Path.Combine(folder, "secrets.json");
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize(File.ReadAllText(_file), CoreJson.Default.DictionaryStringString) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void Write(Dictionary<string, string> all)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, CoreJson.Default.DictionaryStringString));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _file, true);
    }

    public string? Get(string id)
    {
        lock (_lock)
        {
            if (!Read().TryGetValue(id, out var stored)) return null;
            try
            {
                var bytes = Convert.FromBase64String(stored);
                if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }

    /// <summary>Null or empty removes it.</summary>
    public void Set(string id, string? password)
    {
        lock (_lock)
        {
            var all = Read();
            if (string.IsNullOrEmpty(password)) all.Remove(id);
            else
            {
                var bytes = Encoding.UTF8.GetBytes(password);
                if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                all[id] = Convert.ToBase64String(bytes);
            }
            Write(all);
        }
    }
}
