using ErpPrinter.Http;
using ErpPrinter.Logging;

namespace ErpPrinter.Backends;

/// <summary>The four built-in backends, found by the profile's <c>Backend</c> setting (case-insensitive).</summary>
public sealed class BackendRegistry : IBackendProvider
{
    private readonly Dictionary<string, IBackend> _backends;

    /// <param name="http">The shared transport; tests pass one over a fake handler.</param>
    /// <param name="directory">Active Directory lookup for the Odoo user mapping; none by default.</param>
    public BackendRegistry(ErpHttp http, TimeProvider? time = null, IDirectoryIdentity? directory = null, IErpLog? log = null)
    {
        var odoo = new OdooCommon(time, directory, log);
        IBackend[] all =
        [
            new OdooJson2Backend(http, odoo),
            new OdooJsonRpcBackend(http, odoo),
            new ERPNextBackend(http, time),
            new WebhookBackend(http),
        ];
        _backends = all.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IBackend Get(string backendName) =>
        _backends.TryGetValue(backendName ?? "", out var backend)
            ? backend
            : throw new ErpException($"Backend '{backendName}' is not available in this version.", permanent: true);
}
