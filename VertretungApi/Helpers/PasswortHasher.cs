using System.Security.Cryptography;

namespace VertretungApi.Helpers;

// PBKDF2-Passworthash (ohne Zusatzpaket). Format: "iter.saltB64.hashB64".
public static class PasswortHasher
{
    private const int Iterationen = 100_000;
    private const int SaltLen = 16;
    private const int HashLen = 32;

    public static string Hash(string passwort)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(passwort, salt, Iterationen, HashAlgorithmName.SHA256, HashLen);
        return $"{Iterationen}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string passwort, string gespeichert)
    {
        var teile = gespeichert.Split('.');
        if (teile.Length != 3) return false;
        try
        {
            int iter = int.Parse(teile[0]);
            byte[] salt = Convert.FromBase64String(teile[1]);
            byte[] hash = Convert.FromBase64String(teile[2]);
            byte[] test = Rfc2898DeriveBytes.Pbkdf2(passwort, salt, iter, HashAlgorithmName.SHA256, hash.Length);
            return CryptographicOperations.FixedTimeEquals(test, hash);
        }
        catch { return false; }
    }
}
