using System.Text;
using HvPortal.Gateway.Crypto;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Passwords of remote Hyper-V hosts (ADR-022): AES-256-GCM with the gateway key ring; the purpose (AAD) binds the
/// ciphertext to the host id, so a value copied to another host row does not decrypt. Plain text exists only while a
/// management agent's target list is being sent over its mTLS channel. Never logged, audited or returned by the API.
/// </summary>
public static class RemoteHostCredentials
{
    private static string Purpose(Guid hostId) => "remote-host-password:" + hostId.ToString("D");

    public static byte[] Protect(KeyRing keys, Guid hostId, string password) => keys.Encrypt(Encoding.UTF8.GetBytes(password), Purpose(hostId));

    /// <exception cref="System.Security.Cryptography.CryptographicException">Wrong key ring or tampered value.</exception>
    public static string Unprotect(KeyRing keys, Guid hostId, byte[] encrypted) => Encoding.UTF8.GetString(keys.Decrypt(encrypted, Purpose(hostId)));
}
