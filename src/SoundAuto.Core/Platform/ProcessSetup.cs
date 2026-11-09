using System.Runtime.InteropServices;

namespace SoundAuto.Platform;

/// <summary>Process-wide tweaks for a long-running background audio app.</summary>
public static partial class ProcessSetup
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;

    /// <summary>
    /// Windows 11 slows down timers in processes without a visible window ("EcoQoS" / efficiency mode).
    /// The audio threads are protected by MMCSS anyway, but our control loop (device watching, meters, logging)
    /// would otherwise stall for seconds at a time. Opting out keeps it on schedule; the loop itself is
    /// cheap, so the battery impact is negligible.
    /// </summary>
    public static void DisableBackgroundThrottling()
    {
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution,
            StateMask = 0, // 0 = "don't throttle" for every bit set in ControlMask
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>
    /// Ensures only one copy runs per user. Two copies would both capture the cable and play into the
    /// speakers, so you'd hear everything twice. Dispose the returned object at exit; null means another copy is running.
    /// </summary>
    public static IDisposable? TryAcquireSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\SpicaSync.SoundAuto", out bool createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
