using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VoicemeeterDelay;

internal sealed class VoicemeeterRemote : IDisposable
{
    private readonly nint _library;
    private nint _callbackClientNameBuffer;
    private bool _disposed;

    private VoicemeeterRemote(nint library)
    {
        _library = library;
        _login = LoadDelegate<LoginDelegate>("VBVMR_Login");
        _logout = LoadDelegate<LogoutDelegate>("VBVMR_Logout");
        _getVoicemeeterType = LoadDelegate<GetVoicemeeterTypeDelegate>("VBVMR_GetVoicemeeterType");
        _audioCallbackRegister = LoadDelegate<AudioCallbackRegisterDelegate>("VBVMR_AudioCallbackRegister");
        _audioCallbackStart = LoadDelegate<AudioCallbackStartDelegate>("VBVMR_AudioCallbackStart");
        _audioCallbackStop = LoadDelegate<AudioCallbackStopDelegate>("VBVMR_AudioCallbackStop");
        _audioCallbackUnregister = LoadDelegate<AudioCallbackUnregisterDelegate>("VBVMR_AudioCallbackUnregister");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int AudioCallback(nint user, int command, nint data, int unused);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVoicemeeterTypeDelegate(out int voicemeeterType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCallbackRegisterDelegate(int mode, AudioCallback callback, nint user, nint clientName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCallbackStartDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCallbackStopDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCallbackUnregisterDelegate();

    private readonly LoginDelegate _login;

    private readonly LogoutDelegate _logout;

    private readonly GetVoicemeeterTypeDelegate _getVoicemeeterType;

    private readonly AudioCallbackRegisterDelegate _audioCallbackRegister;

    private readonly AudioCallbackStartDelegate _audioCallbackStart;

    private readonly AudioCallbackStopDelegate _audioCallbackStop;

    private readonly AudioCallbackUnregisterDelegate _audioCallbackUnregister;

    public static VoicemeeterRemote Load(string? explicitPath)
    {
        var attempted = new List<string>();

        foreach (var candidate in GetCandidateDllPaths(explicitPath))
        {
            try
            {
                nint handle;
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    handle = NativeLibrary.Load(candidate);
                }
                else if (!Path.IsPathFullyQualified(candidate))
                {
                    handle = NativeLibrary.Load(candidate);
                }
                else
                {
                    attempted.Add(candidate);
                    continue;
                }

                return new VoicemeeterRemote(handle);
            }
            catch
            {
                attempted.Add(candidate);
            }
        }

        throw new FileNotFoundException(
            "Could not load VoicemeeterRemote64.dll. Install Voicemeeter, copy the DLL beside this app, or pass --dll PATH."
            + Environment.NewLine
            + "Tried:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, attempted.Distinct().Select(path => $"  {path}")));
    }

    public int Login()
    {
        ThrowIfDisposed();
        return _login();
    }

    public int Logout()
    {
        if (_disposed)
        {
            return 0;
        }

        return _logout();
    }

    public int GetVoicemeeterType(out VoicemeeterKind voicemeeterKind)
    {
        ThrowIfDisposed();

        var result = _getVoicemeeterType(out var rawType);
        voicemeeterKind = VoicemeeterKindInfo.FromApiValue(rawType);
        return result;
    }

    public int AudioCallbackRegister(int mode, AudioCallback callback, string clientName)
    {
        ThrowIfDisposed();

        FreeCallbackClientName();
        _callbackClientNameBuffer = AllocateAnsiString(clientName, maxBytes: 63);
        try
        {
            var result = _audioCallbackRegister(mode, callback, 0, _callbackClientNameBuffer);
            if (result != 0)
            {
                FreeCallbackClientName();
            }

            return result;
        }
        catch
        {
            FreeCallbackClientName();
            throw;
        }
    }

    private static nint AllocateAnsiString(string text, int? maxBytes = null)
    {
        var nameBytes = Encoding.ASCII.GetBytes(text);
        if (maxBytes is { } limit && nameBytes.Length > limit)
        {
            nameBytes = nameBytes[..limit];
        }

        var bufferLength = nameBytes.Length + 1;
        var buffer = Marshal.AllocHGlobal(bufferLength);

        unsafe
        {
            var target = (byte*)buffer;
            for (var i = 0; i < bufferLength - 1; i++)
            {
                target[i] = nameBytes[i];
            }

            target[bufferLength - 1] = 0;
        }

        return buffer;
    }

    public int AudioCallbackStart()
    {
        ThrowIfDisposed();
        return _audioCallbackStart();
    }

    public int AudioCallbackStop()
    {
        if (_disposed)
        {
            return 0;
        }

        return _audioCallbackStop();
    }

    public int AudioCallbackUnregister()
    {
        if (_disposed)
        {
            return 0;
        }

        var result = _audioCallbackUnregister();
        FreeCallbackClientName();
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FreeCallbackClientName();
        NativeLibrary.Free(_library);
        _disposed = true;
    }

    private void FreeCallbackClientName()
    {
        if (_callbackClientNameBuffer == 0)
        {
            return;
        }

        Marshal.FreeHGlobal(_callbackClientNameBuffer);
        _callbackClientNameBuffer = 0;
    }

    private T LoadDelegate<T>(string exportName)
        where T : Delegate
    {
        var export = NativeLibrary.GetExport(_library, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static IEnumerable<string> GetCandidateDllPaths(string? explicitPath)
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
            yield return Path.Combine(programFilesX86, "VB", "Voicemeeter", "VoicemeeterRemote.dll");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
            yield return Path.Combine(programFiles, "VB", "Voicemeeter", "VoicemeeterRemote.dll");
        }

        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "VoicemeeterRemote64.dll");
        yield return Path.Combine(baseDirectory, "VoicemeeterRemote.dll");

        foreach (var installDirectory in GetInstallDirectoriesFromRegistry())
        {
            yield return Path.Combine(installDirectory, "VoicemeeterRemote64.dll");
            yield return Path.Combine(installDirectory, "VoicemeeterRemote.dll");
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
        }

        yield return "VoicemeeterRemote64.dll";
        yield return "VoicemeeterRemote.dll";
    }

    private static IEnumerable<string> GetInstallDirectoriesFromRegistry()
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var displayName = appKey.GetValue("DisplayName") as string;
                    if (displayName?.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    foreach (var directory in ReadInstallDirectories(appKey))
                    {
                        yield return directory;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ReadInstallDirectories(RegistryKey appKey)
    {
        foreach (var valueName in new[] { "InstallLocation", "InstallSource" })
        {
            if (appKey.GetValue(valueName) is string path && Directory.Exists(path))
            {
                yield return path;
            }
        }

        if (appKey.GetValue("UninstallString") is not string uninstallString)
        {
            yield break;
        }

        var executable = ExtractExecutablePath(uninstallString);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static string? ExtractExecutablePath(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return null;
        }

        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? command[..(exeIndex + 4)] : command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VoicemeeterRemote));
        }
    }
}
