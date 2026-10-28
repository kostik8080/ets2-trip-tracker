using System.Security.Cryptography;

namespace EtsTracker;

/// <summary>
/// PIN-код администратора. В базе хранится не сам PIN, а «соль:хеш» (PBKDF2-SHA256),
/// поэтому из файла базы PIN не прочитать.
/// </summary>
public static class Pin
{
    public const int MinLength = 4;
    public const int MaxLength = 8;
    private const int Iterations = 100_000;

    public static bool IsValid(string pin)
        => pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
