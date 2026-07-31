using System.Linq;

namespace DevStack.API.Models;

// Shared password rules for anything we let users set. Deliberately strong for
// the people who own accounts (owner / platform admin); staff authenticate with
// a PIN instead, so they're not forced to type a long password on a shared POS.
public static class PasswordPolicy
{
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            return "Password must be at least 10 characters.";
        if (!password.Any(char.IsUpper))
            return "Password must contain an uppercase letter.";
        if (!password.Any(char.IsLower))
            return "Password must contain a lowercase letter.";
        if (!password.Any(char.IsDigit))
            return "Password must contain a number.";
        return null;
    }
}
