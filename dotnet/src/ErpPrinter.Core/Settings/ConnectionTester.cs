using ErpPrinter.Backends;
using ErpPrinter.Secrets;

namespace ErpPrinter.Settings;

public sealed record ConnectionResult(bool Success, string Message);

/// <summary>
/// Checks that a profile's ERP is reachable and the credentials work. Port of Test-ErpPrinterConnection: the profile is
/// validated first, unsaved credentials (typed in the GUI but not saved yet) take precedence over stored ones, and every
/// failure comes back as a message instead of an exception.
/// </summary>
public sealed class ConnectionTester(IBackendProvider backends, SecretStore secrets)
{
    public async Task<ConnectionResult> TestAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets? unsavedSecrets = null, CancellationToken cancellationToken = default)
    {
        var effective = secrets.Effective(profile.Name, unsavedSecrets);
        var problems = ProfileValidator.Validate(profile, name => name switch
        {
            SecretNames.ApiKey => effective.ApiKey.Length > 0,
            SecretNames.ApiSecret => effective.ApiSecret.Length > 0,
            _ => false,
        });
        if (problems.Count > 0) return new ConnectionResult(false, string.Join(Environment.NewLine, problems));
        try
        {
            var message = await backends.Get(profile.Backend).TestConnectionAsync(profile, global, effective, cancellationToken).ConfigureAwait(false);
            return new ConnectionResult(true, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, ex.Message);
        }
    }
}
