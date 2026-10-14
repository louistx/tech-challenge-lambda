using System.Text.RegularExpressions;

namespace TechChallenge.Lambda;

public static partial class Cpf
{
    [GeneratedRegex(@"\A(?:[0-9]{11}|[0-9]{3}\.[0-9]{3}\.[0-9]{3}-[0-9]{2})\z")]
    private static partial Regex Format();

    public static string Normalize(string? value)
    {
        if (value is null || !Format().IsMatch(value)) throw new AuthException("invalid_cpf", 400);
        var digits = value.Replace(".", "").Replace("-", "");
        if (digits.Distinct().Count() == 1) throw new AuthException("invalid_cpf", 400);
        for (var size = 9; size <= 10; size++)
        {
            var sum = 0;
            for (var i = 0; i < size; i++) sum += (digits[i] - '0') * (size + 1 - i);
            if (sum * 10 % 11 % 10 != digits[size] - '0') throw new AuthException("invalid_cpf", 400);
        }
        return digits;
    }
}

public sealed class AuthException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
