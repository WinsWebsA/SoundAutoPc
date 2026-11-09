using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SoundAuto.Platform;

/// <summary>
/// Changes the Windows default playback device, the same thing as picking a device in the taskbar sound flyout.
///
/// Windows has no public API for this. The IPolicyConfig COM interface used here is what the Sound control
/// panel itself uses, and has been stable since Windows 7. Tools like EarTrumpet and SoundSwitch rely on it too.
/// If it ever stops working, callers get a clear error and can ask the user to switch manually.
/// </summary>
public static class DefaultDeviceSwitcher
{
    /// <summary>
    /// Sets the device as default for media/games (Console) and music/video (Multimedia). The Communications role
    /// is deliberately left alone, so calls in Teams/Zoom/WhatsApp keep going straight to your real device.
    /// </summary>
    public static void SetDefault(string deviceId)
    {
        object? instance = null;
        try
        {
            instance = new PolicyConfigClient();
            var config = (IPolicyConfig)instance;
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, Role.Console));
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, Role.Multimedia));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new InvalidOperationException(
                $"Windows refused to change the default sound device ({ex.Message}). Choose it manually in the taskbar sound menu.", ex);
        }
        finally
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    /// <summary>Only SetDefaultEndpoint is called; the earlier methods exist to keep the vtable layout right.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
