using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Valencia.Obs;

public sealed record ObsSavedConnection(int Version, string Endpoint, string ProtectedPassword);

/// <summary>Windows user-scoped DPAPI. The password never belongs in a project, manifest or CLI argument.</summary>
[SupportedOSPlatform("windows")]
public static class ObsCredentials
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "obs-connection.json");
    public static async Task SaveAsync(Uri endpoint, string password, string? path = null, CancellationToken cancellation = default)
    {
        new ObsConnectionOptions(endpoint).Validate(password.Length > 0);
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            string encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            await ObsManifestFile.WriteAtomicAsync(path ?? DefaultPath, new ObsSavedConnection(1, endpoint.ToString(), encrypted), cancellation).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static (ObsConnectionOptions Options, string Password)? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("OBS connection configuration exceeds its budget.");
        var config = JsonSerializer.Deserialize<ObsSavedConnection>(File.ReadAllText(path), ObsManifestFile.Json)
            ?? throw new InvalidDataException("Invalid OBS connection settings.");
        if (config.Version != 1) throw new InvalidDataException("Unsupported OBS connection settings version.");
        byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(config.ProtectedPassword), null, DataProtectionScope.CurrentUser);
        try { return (new(new Uri(config.Endpoint)), Encoding.UTF8.GetString(decrypted)); }
        finally { CryptographicOperations.ZeroMemory(decrypted); }
    }
}
