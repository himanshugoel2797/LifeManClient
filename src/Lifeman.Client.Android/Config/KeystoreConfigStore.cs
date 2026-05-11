using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Lifeman.Client.Config;

namespace Lifeman.Client.Android.Config;

/// SharedPreferences-backed config with sensitive values wrapped in
/// AES-GCM using a Keystore-resident key. The key never leaves the
/// trusted execution environment; only the ciphertext lands in prefs.
///
/// On-disk shape (within SharedPreferences):
///   "server.base_url" -> "http://..."        (plain)
///   "device.token"    -> "v1:&lt;iv-base64&gt;:&lt;ct-base64&gt;"
///
/// AES/GCM/NoPadding with a 12-byte random IV per write — required for
/// GCM nonce uniqueness; reusing an IV under the same key catastrophically
/// breaks GCM authentication.
public sealed class KeystoreConfigStore : IConfigStore
{
    private const string KeyAlias = "lifeman.client.device_token.v1";
    private const string AndroidKeystore = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLengthBytes = 12;
    private const int TagLengthBits = 128;

    private readonly ISharedPreferences _prefs;

    public KeystoreConfigStore(Context ctx)
    {
        _prefs = ctx.GetSharedPreferences("lifeman.client", FileCreationMode.Private)
            ?? throw new InvalidOperationException("SharedPreferences unavailable");
        EnsureKey();
    }

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var raw = _prefs.GetString(key, null);
        if (raw is null) return ValueTask.FromResult<string?>(null);
        if (ConfigKeys.IsSensitive(key))
        {
            try { return ValueTask.FromResult<string?>(Decrypt(raw)); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("lifeman", $"decrypt failed for {key}: {ex.Message}");
                return ValueTask.FromResult<string?>(null);
            }
        }
        return ValueTask.FromResult<string?>(raw);
    }

    public ValueTask SetAsync(string key, string value, CancellationToken ct = default)
    {
        var stored = ConfigKeys.IsSensitive(key) ? Encrypt(value) : value;
        var ed = _prefs.Edit() ?? throw new InvalidOperationException();
        ed.PutString(key, stored);
        ed.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        var ed = _prefs.Edit() ?? throw new InvalidOperationException();
        ed.Remove(key);
        ed.Commit();
        return ValueTask.CompletedTask;
    }

    private static void EnsureKey()
    {
        var ks = KeyStore.GetInstance(AndroidKeystore)!;
        ks.Load(null);
        if (ks.ContainsAlias(KeyAlias)) return;

        var spec = new KeyGenParameterSpec.Builder(KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)!
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build()!;
        var gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, AndroidKeystore)!;
        gen.Init(spec);
        gen.GenerateKey();
    }

    private static IKey LoadKey()
    {
        var ks = KeyStore.GetInstance(AndroidKeystore)!;
        ks.Load(null);
        return ks.GetKey(KeyAlias, null)
            ?? throw new InvalidOperationException("keystore key missing");
    }

    private static string Encrypt(string plaintext)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, LoadKey());
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("cipher missing IV");
        var ct = cipher.DoFinal(Encoding.UTF8.GetBytes(plaintext))!;
        return $"v1:{Convert.ToBase64String(iv)}:{Convert.ToBase64String(ct)}";
    }

    private static string Decrypt(string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 3 || parts[0] != "v1")
            throw new InvalidOperationException("malformed ciphertext");
        var iv = Convert.FromBase64String(parts[1]);
        var ct = Convert.FromBase64String(parts[2]);
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, LoadKey(), new GCMParameterSpec(TagLengthBits, iv));
        var pt = cipher.DoFinal(ct)!;
        return Encoding.UTF8.GetString(pt);
    }
}
