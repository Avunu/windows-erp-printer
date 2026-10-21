namespace ErpPrinter.Backends;

/// <summary>Finds the backend implementation for a profile's <c>Backend</c> setting. <c>BackendRegistry</c> is the real one.</summary>
public interface IBackendProvider
{
    /// <summary>Throws a permanent <see cref="ErpException"/> when the backend is not available in this version.</summary>
    IBackend Get(string backendName);
}
