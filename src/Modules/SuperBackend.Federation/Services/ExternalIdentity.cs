using System.Security.Cryptography;
using System.Text;

namespace SuperBackend.Federation;

internal static class ExternalIdentity
{
    public static string Key(string issuer, string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject)));
}
