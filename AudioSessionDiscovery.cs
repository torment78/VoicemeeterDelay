using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VoicemeeterDelay;

internal sealed record AudioSessionList(string EndpointName, IReadOnlyList<AudioSessionApp> Apps);

internal sealed record AudioSessionApp(string Name, string State);

internal enum AudioEndpointFlow
{
    Playback,
    Capture
}

internal sealed record AudioEndpointChoice(string Id, string Name, AudioEndpointFlow Flow)
{
    public override string ToString()
    {
        return Name;
    }
}

internal static class AudioSessionDiscovery
{
    private const string AudioDevicesRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string DeviceDescriptionValueName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string DeviceFriendlyNameValueName = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";
    private const string DeviceInterfaceFriendlyNameValueName = "{026e516e-b814-414b-83cd-856d6fef4822},2";
    private const int ActiveDeviceState = 1;

    private static readonly Guid AudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
    private static readonly PropertyKey DeviceDescriptionKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        2);
    private static readonly PropertyKey DeviceInterfaceFriendlyNameKey = new(
        new Guid("026E516E-B814-414B-83CD-856D6FEF4822"),
        2);

    public static AudioSessionList? GetVoicemeeterInputExtensionSessions(int inputNumber)
    {
        return GetRenderSessions(names => IsVoicemeeterInputExtension(names, inputNumber))
            .FirstOrDefault();
    }

    public static IReadOnlyList<AudioEndpointChoice> GetVoicemeeterEndpointChoices(AudioEndpointFlow flow)
    {
        Func<IReadOnlyList<string>, bool> filter = flow == AudioEndpointFlow.Playback
            ? IsVoicemeeterPingInputEndpoint
            : IsVoicemeeterReturnOutputEndpoint;
        return GetEndpointChoicesFromRegistry(flow, filter);
    }

    private static IReadOnlyList<AudioEndpointChoice> GetEndpointChoicesFromRegistry(
        AudioEndpointFlow flow,
        Func<IReadOnlyList<string>, bool> includeEndpoint)
    {
        var endpointKind = flow == AudioEndpointFlow.Playback ? "Render" : "Capture";
        var endpointIdPrefix = flow == AudioEndpointFlow.Playback
            ? "{0.0.0.00000000}."
            : "{0.0.1.00000000}.";
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var devicesKey = baseKey.OpenSubKey($@"{AudioDevicesRegistryPath}\{endpointKind}");
        if (devicesKey is null)
        {
            return [];
        }

        var endpoints = new List<AudioEndpointChoice>();
        foreach (var keyName in devicesKey.GetSubKeyNames())
        {
            using var endpointKey = devicesKey.OpenSubKey(keyName);
            if (endpointKey is null || !IsActiveEndpoint(endpointKey))
            {
                continue;
            }

            using var propertiesKey = endpointKey.OpenSubKey("Properties");
            var names = ReadEndpointNames(propertiesKey, keyName);
            var endpointId = endpointIdPrefix + keyName;
            if (!includeEndpoint([.. names, endpointId]))
            {
                continue;
            }

            endpoints.Add(new AudioEndpointChoice(endpointId, GetDisplayName(names), flow));
        }

        return endpoints
            .GroupBy(static endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool IsActiveEndpoint(RegistryKey endpointKey)
    {
        return endpointKey.GetValue("DeviceState") is int state && state == ActiveDeviceState;
    }

    private static IReadOnlyList<string> ReadEndpointNames(RegistryKey? propertiesKey, string keyName)
    {
        if (propertiesKey is null)
        {
            return [keyName];
        }

        return new[]
            {
                ReadRegistryString(propertiesKey, DeviceFriendlyNameValueName),
                ReadRegistryString(propertiesKey, DeviceInterfaceFriendlyNameValueName),
                ReadRegistryString(propertiesKey, DeviceDescriptionValueName),
                keyName
            }
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadRegistryString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) as string ?? string.Empty;
    }

    private static IReadOnlyList<AudioEndpointChoice> GetEndpointChoices(
        EDataFlow dataFlow,
        AudioEndpointFlow flow,
        Func<IReadOnlyList<string>, bool> includeEndpoint)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        var endpoints = new List<AudioEndpointChoice>();

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(
                dataFlow,
                DeviceState.Active,
                out devices));

            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (var index = 0u; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(devices.Item(index, out var device));
                try
                {
                    var endpointNames = GetDeviceNames(device);
                    if (!includeEndpoint(endpointNames))
                    {
                        continue;
                    }

                    endpoints.Add(new AudioEndpointChoice(
                        GetDeviceId(device),
                        GetDisplayName(endpointNames),
                        flow));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (devices is not null)
            {
                Marshal.ReleaseComObject(devices);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        return endpoints
            .OrderBy(static endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AudioSessionList> GetRenderSessions(Func<IReadOnlyList<string>, bool> includeEndpoint)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        var endpointSessions = new List<AudioSessionList>();

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(
                EDataFlow.Render,
                DeviceState.Active,
                out devices));

            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (var index = 0u; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(devices.Item(index, out var device));
                try
                {
                    var endpointNames = GetDeviceNames(device);
                    if (!includeEndpoint(endpointNames))
                    {
                        continue;
                    }

                    endpointSessions.Add(new AudioSessionList(GetDisplayName(endpointNames), ReadSessionsSafely(device)));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (devices is not null)
            {
                Marshal.ReleaseComObject(devices);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        return endpointSessions
            .OrderBy(static endpoint => endpoint.EndpointName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AudioSessionApp> ReadSessionsSafely(IMMDevice device)
    {
        try
        {
            return GetSessions(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return
            [
                new AudioSessionApp(CleanErrorMessage(ex.Message), "Read error")
            ];
        }
    }

    private static IReadOnlyList<AudioSessionApp> GetSessions(IMMDevice device)
    {
        object? managerObject = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        var apps = new List<AudioSessionApp>();

        try
        {
            var managerId = AudioSessionManager2Id;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref managerId,
                ClsCtx.InprocServer,
                0,
                out managerObject));
            manager = (IAudioSessionManager2)managerObject;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));

            for (var index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(sessions.GetSession(index, out var session));
                try
                {
                    var app = GetSessionApp(session);
                    if (app is not null)
                    {
                        apps.Add(app);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        finally
        {
            if (sessions is not null)
            {
                Marshal.ReleaseComObject(sessions);
            }

            if (managerObject is not null)
            {
                Marshal.ReleaseComObject(managerObject);
            }
        }

        return apps
            .OrderBy(static app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static app => app.State, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static AudioSessionApp? GetSessionApp(IAudioSessionControl2 session)
    {
        Marshal.ThrowExceptionForHR(session.GetState(out var state));
        if (state == AudioSessionState.Expired)
        {
            return null;
        }

        Marshal.ThrowExceptionForHR(session.GetProcessId(out var processId));
        var name = processId == 0
            ? ReadDisplayName(session)
            : GetProcessName(processId);

        if (processId == 0 && IsSystemAudioResourceName(name))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = processId == 0 ? "System sounds" : $"PID {processId}";
        }

        return new AudioSessionApp(name, state.ToString());
    }

    private static bool IsSystemAudioResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.ToLowerInvariant();
        return normalized.Contains("audioses.dll", StringComparison.Ordinal)
            || normalized.Contains("%systemroot%", StringComparison.Ordinal)
            || normalized.Contains("system32\\audio", StringComparison.Ordinal)
            || normalized.Contains("system32/audio", StringComparison.Ordinal);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? $"{process.ProcessName}.exe"
                : $"{process.ProcessName}.exe - {process.MainWindowTitle}";
        }
        catch
        {
            return $"PID {processId}";
        }
    }

    private static string ReadDisplayName(IAudioSessionControl2 session)
    {
        if (session.GetDisplayName(out var displayNamePointer) != 0 || displayNamePointer == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(displayNamePointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(displayNamePointer);
        }
    }

    private static IReadOnlyList<string> GetDeviceNames(IMMDevice device)
    {
        IPropertyStore? propertyStore = null;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out propertyStore));
            return new[]
                {
                    ReadStringProperty(propertyStore, DeviceFriendlyNameKey),
                    ReadStringProperty(propertyStore, DeviceInterfaceFriendlyNameKey),
                    ReadStringProperty(propertyStore, DeviceDescriptionKey),
                    GetDeviceId(device)
                }
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (propertyStore is not null)
            {
                Marshal.ReleaseComObject(propertyStore);
            }
        }
    }

    private static string ReadStringProperty(IPropertyStore propertyStore, PropertyKey key)
    {
        if (propertyStore.GetValue(ref key, out var propertyValue) != 0)
        {
            return string.Empty;
        }

        try
        {
            return propertyValue.ValuePointer == 0
                ? string.Empty
                : Marshal.PtrToStringUni(propertyValue.ValuePointer) ?? string.Empty;
        }
        finally
        {
            PropVariantClear(ref propertyValue);
        }
    }

    private static string GetDeviceId(IMMDevice device)
    {
        if (device.GetId(out var idPointer) != 0 || idPointer == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(idPointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(idPointer);
        }
    }

    private static string GetDisplayName(IReadOnlyList<string> endpointNames)
    {
        return endpointNames.Count == 0 ? "Unknown endpoint" : endpointNames[0];
    }

    private static bool IsVoicemeeterEndpoint(IReadOnlyList<string> endpointNames)
    {
        return endpointNames
            .Select(NormalizeEndpointName)
            .Any(static name => name.Contains("voicemeeter", StringComparison.Ordinal)
                || name.Contains("vbaudio", StringComparison.Ordinal));
    }

    private static bool IsVoicemeeterPingInputEndpoint(IReadOnlyList<string> endpointNames)
    {
        return IsVoicemeeterEndpoint(endpointNames)
            && endpointNames
                .Select(NormalizeEndpointName)
                .Any(static name => name.Contains("input", StringComparison.Ordinal)
                    || name.Contains("in", StringComparison.Ordinal)
                    || name.Contains("vaio", StringComparison.Ordinal));
    }

    private static bool IsVoicemeeterReturnOutputEndpoint(IReadOnlyList<string> endpointNames)
    {
        return IsVoicemeeterEndpoint(endpointNames)
            && endpointNames
                .Select(NormalizeEndpointName)
                .Any(static name => name.Contains("output", StringComparison.Ordinal)
                    || name.Contains("out", StringComparison.Ordinal)
                    || name.Contains("b1", StringComparison.Ordinal)
                    || name.Contains("b2", StringComparison.Ordinal)
                    || name.Contains("b3", StringComparison.Ordinal)
                    || name.Contains("a1", StringComparison.Ordinal)
                    || name.Contains("a2", StringComparison.Ordinal)
                    || name.Contains("a3", StringComparison.Ordinal)
                    || name.Contains("a4", StringComparison.Ordinal)
                    || name.Contains("a5", StringComparison.Ordinal));
    }

    private static bool IsVoicemeeterInputExtension(IReadOnlyList<string> endpointNames, int inputNumber)
    {
        return endpointNames.Any(endpointName => IsVoicemeeterInputExtension(endpointName, inputNumber));
    }

    private static bool IsVoicemeeterInputExtension(string endpointName, int inputNumber)
    {
        var compact = NormalizeEndpointName(endpointName);
        var aliases = new[]
        {
            $"in{inputNumber}",
            $"input{inputNumber}",
            $"virtualinput{inputNumber}",
            $"vaio{inputNumber}",
            $"extensioninput{inputNumber}",
            $"vaioextension{inputNumber}",
            $"voicemeeterin{inputNumber}",
            $"voicemeeterinput{inputNumber}"
        };

        return compact.Contains("voicemeeter", StringComparison.Ordinal)
            && aliases.Any(alias => compact.Contains(alias, StringComparison.Ordinal));
    }

    private static string NormalizeEndpointName(string endpointName)
    {
        return new string(endpointName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static string CleanErrorMessage(string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "Endpoint does not expose Windows audio sessions"
            : message.Replace(Environment.NewLine, " ");
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    [Flags]
    private enum DeviceState
    {
        Active = 1
    }

    [Flags]
    private enum ClsCtx
    {
        InprocServer = 1
    }

    private enum AudioSessionState
    {
        Inactive = 0,
        Active = 1,
        Expired = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        private readonly Guid _formatId = formatId;
        private readonly int _propertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private readonly ushort _variantType;
        private readonly ushort _reserved1;
        private readonly ushort _reserved2;
        private readonly ushort _reserved3;
        public nint ValuePointer;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint deviceIndex, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, ClsCtx classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

        [PreserveSig]
        int OpenPropertyStore(int storageAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out nint id);

        [PreserveSig]
        int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetAt(int propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out nint displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, nint eventContext);

        [PreserveSig]
        int GetIconPath(out nint iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, nint eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(Guid groupingId, nint eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint newNotifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint newNotifications);

        [PreserveSig]
        int GetSessionIdentifier(out nint sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);
    }
}
