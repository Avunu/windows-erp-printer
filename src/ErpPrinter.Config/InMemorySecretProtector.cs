using System.Text;
using ErpPrinter.Secrets;

namespace ErpPrinter.Config;

/// <summary>A reversible stand-in for DPAPI, for <c>--demo</c> and tests. It does not protect anything.</summary>
internal sealed class InMemorySecretProtector : ISecretProtector
{
    private const string Prefix = "demo:";

    public byte[] Protect(string plainText) => Encoding.UTF8.GetBytes(Prefix + plainText);

    public string Unprotect(byte[] cipher)
    {
        var text = Encoding.UTF8.GetString(cipher);
        return text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : throw new InvalidOperationException("Not a demo secret.");
    }
}
