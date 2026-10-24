using System.Runtime.InteropServices;

namespace Pennyworth.Monitoring.Native;

/// <summary>Which Windows services run in which process (for expanding "Windows services").</summary>
internal static unsafe partial class ServiceReader
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 0x1;
    private const int ErrorMoreData = 234;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(
        nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services, int bufferSize,
        out int bytesNeeded, out int servicesReturned, ref int resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    /// <summary>ENUM_SERVICE_STATUS_PROCESSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    /// <summary>Display names of running services, by process ID. Empty when Windows refuses.</summary>
    public static Dictionary<int, List<string>> Read()
    {
        var result = new Dictionary<int, List<string>>();
        nint manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager == 0)
        {
            return result;
        }

        try
        {
            int resume = 0;
            EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, null, 0, out int needed, out _, ref resume, null);
            if (Marshal.GetLastPInvokeError() != ErrorMoreData)
            {
                return result;
            }

            byte[] buffer = new byte[needed + 4096];
            resume = 0;
            fixed (byte* start = buffer)
            {
                if (!EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, start, buffer.Length, out _, out int count, ref resume, null))
                {
                    return result;
                }

                var services = (ServiceStatusProcess*)start;
                for (int i = 0; i < count; i++)
                {
                    int pid = (int)services[i].ProcessId;
                    if (pid == 0)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(pid, out var names))
                    {
                        result[pid] = names = [];
                    }

                    names.Add(new string(services[i].DisplayName));
                }
            }

            return result;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
