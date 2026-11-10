using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassroomCaption.App;

public static class MicrophoneClientFactory
{
    public static AudioClient OpenRaw(string endpointId)
    {
        nint enumerator = 0, endpoint = 0, instance = 0;
        try
        {
            var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 23, ref iid, out enumerator));
            Marshal.ThrowExceptionForHR(Method<GetDevice>(enumerator, 5)(enumerator, endpointId, out endpoint));
            iid = new Guid("726778CD-F60A-4eda-82DE-E47610CD78AA");
            Marshal.ThrowExceptionForHR(Method<Activate>(endpoint, 3)(endpoint, ref iid, 23, 0, out instance));
            var properties = new AudioClientProperties { cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                eCategory = AudioStreamCategory.Speech, Options = AudioClientStreamOptions.Raw };
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientProperties>());
            try
            {
                Marshal.StructureToPtr(properties, memory, false);
                Marshal.ThrowExceptionForHR(Method<SetProperties>(instance, 16)(instance, memory));
            }
            finally { Marshal.FreeHGlobal(memory); }
            return new AudioClient((IAudioClient)Marshal.GetObjectForIUnknown(instance));
        }
        finally
        {
            if (instance != 0) Marshal.Release(instance);
            if (endpoint != 0) Marshal.Release(endpoint);
            if (enumerator != 0) Marshal.Release(enumerator);
        }
    }
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDevice(nint self, [MarshalAs(UnmanagedType.LPWStr)] string endpointId, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Activate(nint self, ref Guid iid, uint context, nint activationParameters, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetProperties(nint self, nint properties);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
