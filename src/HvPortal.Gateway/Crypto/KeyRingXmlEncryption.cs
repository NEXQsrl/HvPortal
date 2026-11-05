using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace HvPortal.Gateway.Crypto;

/// <summary>
/// ASP.NET Core Data Protection key encryption at rest where DPAPI is not available (Linux container): the XML key
/// material is sealed with the gateway key ring (AES-256-GCM, purpose "dataprotection-xml"), which in turn is
/// protected by the key-encryption key (see <see cref="KeyWrap"/>).
/// </summary>
public sealed class KeyRingXmlEncryptor(KeyRing keys) : IXmlEncryptor
{
    public const string Purpose = "dataprotection-xml";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var sealedBytes = keys.Encrypt(bytes, Purpose);
        var element = new XElement("hvportalEncryptedKey",
            new XComment(" Encrypted with the HvPortal gateway key ring. "),
            new XElement("value", Convert.ToBase64String(sealedBytes)));
        return new EncryptedXmlInfo(element, typeof(KeyRingXmlDecryptor));
    }
}

/// <summary>Instantiated by Data Protection through the service provider.</summary>
public sealed class KeyRingXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var keys = services.GetRequiredService<KeyRing>();
        var value = encryptedElement.Element("value")?.Value ?? throw new InvalidOperationException("Invalid encrypted Data Protection key.");
        var plain = keys.Decrypt(Convert.FromBase64String(value), KeyRingXmlEncryptor.Purpose);
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plain));
    }
}
