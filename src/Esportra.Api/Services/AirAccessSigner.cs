using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>Asymmetric leases can be verified by Air without embedding a signing secret.</summary>
public sealed class AirAccessSigner : IDisposable
{
    private readonly RSA? _key;
    public AirAccessSigner(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var pem = configuration["AirAccess:SigningPrivateKey"];
        if (!string.IsNullOrWhiteSpace(pem)) { _key = RSA.Create(); _key.ImportFromPem(pem.Replace("\\n", "\n")); }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) _key = RSA.Create(3072);
    }
    public object? Sign(Guid userId, Guid deviceId, DateTimeOffset? validUntil, int accountRevision, int deviceRevision)
    {
        if (_key is null) return null;
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddDays(7);
        if (validUntil.HasValue && validUntil.Value < expires) expires = validUntil.Value;
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, userId, deviceId, issuedAt = now, expiresAt = expires, accountRevision, deviceRevision }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new { algorithm = "RS256", keyId = Convert.ToHexString(SHA256.HashData(publicKey)), payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), publicKey = _key.ExportSubjectPublicKeyInfoPem() };
    }
    public void Dispose() => _key?.Dispose();
}
