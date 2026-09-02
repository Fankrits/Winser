using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Winser.Helpers;

/// <summary>
/// Tells Windows that Winser is background work while every one of its windows is minimized.
/// </summary>
/// <remarks>
/// <para>
/// This is the EcoQoS opt-in: a process classified that way is scheduled for efficiency rather
/// than speed - preferring the efficiency cores on a hybrid CPU and running at a lower
/// frequency - which is exactly the right trade for an app nobody is looking at, and exactly
/// the wrong one for an app they are. It is what shows up as "Efficiency mode" in Task
/// Manager.
/// </para>
/// <para>
/// Winser's own process only, not the msedgewebview2 processes that do the actual rendering.
/// Those are where most of the CPU goes, so they are worth a separate, separately measured
/// change; this one is small enough to be obviously safe and is the prerequisite for judging
/// that one.
/// </para>
/// </remarks>
internal static class PowerEfficiency
{
    /// <summary>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling.</summary>
    private const int ProcessPowerThrottling = 4;

    /// <summary>PROCESS_POWER_THROTTLING_CURRENT_VERSION.</summary>
    private const uint CurrentVersion = 1;

    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED.</summary>
    private const uint ExecutionSpeed = 0x1;

    /// <summary>
    /// What was last successfully asked for, so a window activation storm does not turn into a
    /// syscall storm. Null until the first call, which is why it is not simply a bool.
    /// </summary>
    private static bool? _ecoQoSEnabled;

    /// <summary>
    /// Puts this process into EcoQoS, or hands it back to the system's own judgement.
    /// </summary>
    /// <remarks>
    /// The two masks are not a bool in disguise, and the difference matters. Setting
    /// <c>ControlMask = ExecutionSpeed</c> with <c>StateMask = 0</c> does not mean "off" - it
    /// means "never throttle this process", which would pin Winser at high performance for
    /// good and override both Windows' own automatic throttling and the user's Efficiency mode
    /// choice in Task Manager. Clearing <c>ControlMask</c> instead is the documented way back
    /// to system-managed, and that is what off means here.
    /// </remarks>
    public static void SetEcoQoS(bool enabled)
    {
        if (_ecoQoSEnabled == enabled)
        {
            return;
        }

        var state = new PowerThrottlingState
        {
            Version = CurrentVersion,
            ControlMask = enabled ? ExecutionSpeed : 0,
            StateMask = enabled ? ExecutionSpeed : 0,
        };

        try
        {
            if (SetProcessInformation(
                    GetCurrentProcess(),
                    ProcessPowerThrottling,
                    ref state,
                    (uint)Marshal.SizeOf<PowerThrottlingState>()))
            {
                _ecoQoSEnabled = enabled;
                return;
            }

            // Not worth retrying or surfacing: the app is fully functional either way, this
            // only ever costs battery. Left unrecorded so a later call tries again.
            Debug.WriteLine(
                $"[Winser] EcoQoS request ({enabled}) refused: error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older than the API. Stop asking.
            Debug.WriteLine($"[Winser] Power throttling is unavailable on this Windows: {ex.Message}");
            _ecoQoSEnabled = enabled;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref PowerThrottlingState information,
        uint informationSize);

    /// <summary>Returns the current-process pseudo-handle; needs no closing.</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
