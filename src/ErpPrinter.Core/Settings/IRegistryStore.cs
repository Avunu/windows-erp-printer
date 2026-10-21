namespace ErpPrinter.Settings;

public enum RegistryValueType
{
    String,
    DWord,
    Binary,
    MultiString,
}

/// <summary>
/// The registry as the product uses it, with paths relative to HKLM (for example <c>SOFTWARE\ErpPrinter\Printers\Send to Odoo</c>).
/// Everything else goes through this interface so it can be faked in tests; <c>ErpPrinter.Windows</c> has the real one.
/// Values come back as <see cref="string"/>, <see cref="int"/>, <see cref="byte"/>[] or <see cref="string"/>[].
/// </summary>
public interface IRegistryStore
{
    IReadOnlyDictionary<string, object> GetValues(string path);
    IReadOnlyList<string> GetSubKeyNames(string path);
    bool KeyExists(string path);
    void EnsureKey(string path);
    void SetValue(string path, string name, object value, RegistryValueType type);
    void RemoveValue(string path, string name);
    void RemoveKey(string path);

    /// <summary>Creates <c>SOFTWARE\ErpPrinter\Secrets</c> readable only by SYSTEM and Administrators (by SID, so localized Windows works).</summary>
    void EnsureSecretsKey();
}

/// <summary>An in-memory registry for tests and for tools that need to preview changes.</summary>
public sealed class MemoryRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object> GetValues(string path) =>
        _keys.TryGetValue(path, out var values) ? new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>();

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var prefix = path.TrimEnd('\\') + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool KeyExists(string path) => _keys.ContainsKey(path);

    public void EnsureKey(string path)
    {
        if (!_keys.ContainsKey(path)) _keys[path] = new(StringComparer.OrdinalIgnoreCase);
    }

    public void SetValue(string path, string name, object value, RegistryValueType type)
    {
        EnsureKey(path);
        _keys[path][name] = value;
        Kinds[$"{path}|{name}"] = type;
    }

    public void RemoveValue(string path, string name)
    {
        if (_keys.TryGetValue(path, out var values)) values.Remove(name);
    }

    public void RemoveKey(string path)
    {
        foreach (var key in _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(key);
    }

    public void EnsureSecretsKey() => EnsureKey($@"{ConfigStore.RegistryRoot}\Secrets");

    /// <summary>The value type last written for each "path|name", so tests can assert DWord versus String.</summary>
    public Dictionary<string, RegistryValueType> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);
}
