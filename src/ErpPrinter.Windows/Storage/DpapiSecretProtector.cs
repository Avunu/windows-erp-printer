using System.Security.Cryptography;
using System.Text;
using ErpPrinter.Secrets;

namespace ErpPrinter.Windows.Storage;

/// <summary>
/// Machine-scope DPAPI with the entropy of the PowerShell module (<c>ErpPrinter/v1</c>), so secrets stored by the
/// PowerShell product still decrypt and the other way round.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ErpPrinter/v1");

    public byte[] Protect(string plainText) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);

    public string Unprotect(byte[] cipher) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine));
}
