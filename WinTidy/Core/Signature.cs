using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinTidy.Core;

/// <summary>Authenticode (digital signature) checks with caching.</summary>
public static class Signature
{
    static readonly ConcurrentDictionary<string, (bool Signed, string? Publisher)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
    static readonly string WindowsApps = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    /// <summary>Files under C:\Windows are mostly catalog-signed (no embedded signature), so treat them as trusted.</summary>
    public static bool IsWindowsPath(string path) =>
        path.StartsWith(WinDir + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Store apps are protected by package signatures rather than embedded ones.</summary>
    public static bool IsStoreAppPath(string path) =>
        path.StartsWith(WindowsApps + "\\", StringComparison.OrdinalIgnoreCase);

    public static (bool Signed, string? Publisher) Check(string path)
    {
        if (Cache.TryGetValue(path, out var r)) return r;
        bool signed = File.Exists(path) && Verify(path);
        r = (signed, signed ? GetPublisher(path) : null);
        Cache[path] = r;
        return r;
    }

    /// <summary>Signed, or in a location where an embedded signature isn't expected.</summary>
    public static bool IsTrusted(string path) => IsWindowsPath(path) || IsStoreAppPath(path) || Check(path).Signed;

    static bool Verify(string path)
    {
        var fileInfo = new Native.WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);
        var data = new Native.WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_DATA>(),
            dwUIChoice = 2,              // WTD_UI_NONE
            fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
            dwUnionChoice = 1,           // WTD_CHOICE_FILE
            pFile = pFile,
            dwStateAction = 1,           // WTD_STATEACTION_VERIFY
            dwProvFlags = 0x10 | 0x1000, // no revocation check, no network fetches
        };
        try
        {
            var action = GenericVerifyV2;
            int result = Native.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            Native.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure<Native.WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    static string? GetPublisher(string path)
    {
        try
        {
            var subject = X509Certificate.CreateFromSignedFile(path).Subject;
            int i = subject.IndexOf("CN=", StringComparison.Ordinal);
            if (i < 0) return subject;
            i += 3;
            if (i < subject.Length && subject[i] == '"')
            {
                int end = subject.IndexOf('"', i + 1);
                return end > i ? subject[(i + 1)..end] : subject[(i + 1)..];
            }
            int comma = subject.IndexOf(',', i);
            return comma > i ? subject[i..comma] : subject[i..];
        }
        catch { return null; }
    }
}
