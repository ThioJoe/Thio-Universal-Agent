using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Thio_Universal_Agent;

public interface ISecretProvider
{
    void SaveSecret(string keyName, string plainTextSecret, string passwordHash);
    string? LoadSecret(string keyName, string passwordHash);
    bool SecretExists(string keyName);
    void DeleteSecret(string keyName);
}

/// <summary>
/// Provides cross-platform encrypted storage for secrets (such as API Keys) using AES encryption and PBKDF2-SHA256 key derivation.
/// The user will provide a password that will be hashed and used to derive a key. The encrypted file will be saved/read by the backend.
/// The user will have the option to store the hash in the browser and remember it. 
/// This way the actual encrypted file is separated from the browser to protect against XSS and rudimentary stealers, even if user sets to remember the password hash.
/// </summary>
/// <remarks>
/// Stores encrypted secrets in the local application data folder under 'ThioUniversalAgent'. Each secret
/// is encrypted using AES with a key derived from the provided password hash through PBKDF2 with 100,000 iterations.
/// The implementation is compatible with Windows, macOS, and Linux platforms.
/// </remarks>
public class SecretsHandler : ISecretProvider
{
    private static readonly byte[] AppSalt = Encoding.UTF8.GetBytes("ThioAgent_CrossPlatform_Salt_V1!");

    // Automatically resolves to the correct AppData folder on Windows, Mac, and Linux
    private readonly string _appDataFolder;

    public SecretsHandler()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appDataFolder = Path.Combine(localAppData, "ThioUniversalAgent");

        // Ensure the directory exists before we try to read/write to it
        Directory.CreateDirectory(_appDataFolder);
    }

    public void SaveSecret(string keyName, string plainTextSecret, string passwordHash)
    {
        string filePath = Path.Combine(_appDataFolder, $"{keyName}.dat");
        byte[] entropy = Encoding.UTF8.GetBytes(passwordHash);

        using Aes aes = Aes.Create();
        aes.Key = DeriveKey(entropy);
        aes.GenerateIV();

        using FileStream fileStream = new FileStream(filePath, FileMode.Create);
        fileStream.Write(aes.IV, 0, aes.IV.Length);

        using CryptoStream cryptoStream = new CryptoStream(fileStream, aes.CreateEncryptor(), CryptoStreamMode.Write);
        byte[] secretBytes = Encoding.UTF8.GetBytes(plainTextSecret);
        cryptoStream.Write(secretBytes, 0, secretBytes.Length);
    }

    public string? LoadSecret(string keyName, string passwordHash)
    {
        string filePath = Path.Combine(_appDataFolder, $"{keyName}.dat");
        if (!File.Exists(filePath)) return null;

        byte[] entropy = Encoding.UTF8.GetBytes(passwordHash);

        using FileStream fileStream = new FileStream(filePath, FileMode.Open);

        using Aes aes = Aes.Create();
        byte[] iv = new byte[aes.BlockSize / 8];
        if (fileStream.Read(iv, 0, iv.Length) != iv.Length)
            return null;

        aes.Key = DeriveKey(entropy);
        aes.IV = iv;

        try
        {
            using CryptoStream cryptoStream = new CryptoStream(fileStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using StreamReader reader = new StreamReader(cryptoStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public bool SecretExists(string keyName)
    {
        string filePath = Path.Combine(_appDataFolder, $"{keyName}.dat");
        return File.Exists(filePath);
    }

    public void DeleteSecret(string keyName)
    {
        string filePath = Path.Combine(_appDataFolder, $"{keyName}.dat");
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    private static byte[] DeriveKey(byte[] passwordHashEntropy)
    {
        return Rfc2898DeriveBytes.Pbkdf2(passwordHashEntropy, AppSalt, 100_000, HashAlgorithmName.SHA256, 32);
    }
}