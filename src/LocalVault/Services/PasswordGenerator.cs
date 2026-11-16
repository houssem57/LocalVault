using System.Security.Cryptography;

namespace LocalVault.Services;

public sealed record PasswordOptions(int Length = 20, bool Uppercase = true, bool Lowercase = true, bool Digits = true, bool Symbols = true);

public static class PasswordGenerator
{
    public const int MinLength = 12;
    public const int MaxLength = 64;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digit = "23456789";
    private const string Symbol = "!@#$%^&*()-_=+[]{};:,.?/~";

    /// <summary>Generates a password with at least one character from every selected set (CSPRNG, unbiased).</summary>
    public static string Generate(PasswordOptions options)
    {
        int length = Math.Clamp(options.Length, MinLength, MaxLength);
        var sets = new List<string>();
        if (options.Uppercase) sets.Add(Upper);
        if (options.Lowercase) sets.Add(Lower);
        if (options.Digits) sets.Add(Digit);
        if (options.Symbols) sets.Add(Symbol);
        if (sets.Count == 0) sets.Add(Lower);

        string all = string.Concat(sets);
        var chars = new char[length];
        for (int i = 0; i < sets.Count; i++)
            chars[i] = sets[i][RandomNumberGenerator.GetInt32(sets[i].Length)];
        for (int i = sets.Count; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates shuffle so the guaranteed characters are not always at the front.
        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>Rough entropy estimate in bits for UI feedback.</summary>
    public static double EstimateEntropyBits(PasswordOptions options)
    {
        int pool = (options.Uppercase ? Upper.Length : 0) + (options.Lowercase ? Lower.Length : 0)
                 + (options.Digits ? Digit.Length : 0) + (options.Symbols ? Symbol.Length : 0);
        if (pool == 0) pool = Lower.Length;
        return Math.Clamp(options.Length, MinLength, MaxLength) * Math.Log2(pool);
    }
}
