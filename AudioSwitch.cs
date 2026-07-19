using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AirLift;

/// <summary>
/// Switches the Windows default render device via the undocumented (but
/// long-stable, used by AudioSwitcher/SoundSwitch/EarTrumpet) IPolicyConfig COM interface.
/// </summary>
public static class AudioSwitch
{
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Vtable order matters; only SetDefaultEndpoint is used
        int GetMixFormat(string deviceId, IntPtr format);
        int GetDeviceFormat(string deviceId, bool defaultFormat, IntPtr format);
        int ResetDeviceFormat(string deviceId);
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string deviceId, bool defaultPeriod, IntPtr defaultDevicePeriod, IntPtr minimumDevicePeriod);
        int SetProcessingPeriod(string deviceId, IntPtr period);
        int GetShareMode(string deviceId, IntPtr mode);
        int SetShareMode(string deviceId, IntPtr mode);
        int GetPropertyValue(string deviceId, bool bFxStore, IntPtr key, IntPtr value);
        int SetPropertyValue(string deviceId, bool bFxStore, IntPtr key, IntPtr value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        int SetEndpointVisibility(string deviceId, bool visible);
    }

    /// <summary>Makes the given render device the Windows default for all roles.</summary>
    public static void SetDefaultRenderDevice(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public static string GetDefaultRenderDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
    }

    public static bool DeviceExists(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId)?.State == DeviceState.Active;
        }
        catch { return false; }
    }
}
