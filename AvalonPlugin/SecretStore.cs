using System;
using System.Security.Cryptography;
using System.Text;

namespace AvalonPlugin;

/// <summary>Encrypts secrets saved in the plugin config with Windows DPAPI, for the current Windows user only.</summary>
internal static class SecretStore
{
    public static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
