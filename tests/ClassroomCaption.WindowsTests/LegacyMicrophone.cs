using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ClassroomCaption.App;

internal static class LegacyMicrophone
{
    // Match the actual endpoint, never use the default mapper or a truncated friendly name.
    public static int FindDevice(string endpointId)
    {
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            if (string.Equals(GetEndpointId(i), endpointId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public static string? GetEndpointId(int deviceNumber)
    {
        var sizeBuffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(sizeBuffer, 0);
            if (WaveInMessage(deviceNumber, 0x0812, sizeBuffer, 0) != 0) return null;
            var size = Marshal.ReadIntPtr(sizeBuffer).ToInt64();
            if (size < 2 || size > 65536) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                return WaveInMessage(deviceNumber, 0x0811, buffer, (nint)size) == 0 ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(sizeBuffer); }
    }
    [DllImport("winmm.dll", EntryPoint = "waveInMessage")]
    private static extern uint WaveInMessage(nint deviceId, uint message, nint parameter1, nint parameter2);
}
