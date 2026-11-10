using System.Runtime.InteropServices;
using ClassroomCaption.Core;
using Microsoft.Win32.SafeHandles;

namespace ClassroomCaption.App;

// The upstream C API expects PCM16-scale floats, not normalized [-1, 1] floats.
public sealed class RnNoiseFilter : IDisposable
{
    public const int FrameSamples = 480;
    private readonly StateHandle state;
    public RnNoiseFilter()
    {
        try
        {
            if (GetFrameSize() != FrameSamples) throw new UserFacingException("降噪组件版本不匹配，请更新软件。");
            state = Create(IntPtr.Zero);
            if (state.IsInvalid) { state.Dispose(); throw new UserFacingException("无法初始化本地降噪，请关闭降噪后重试。"); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { throw new UserFacingException("本地降噪组件缺失或不兼容，请更新软件；关闭降噪仍可使用原始收音。"); }
    }
    public void Process(float[] input, float[] output)
    {
        ObjectDisposedException.ThrowIf(state.IsClosed, this);
        if (input.Length != FrameSamples || output.Length != FrameSamples) throw new ArgumentException("RNNoise needs 480 samples.");
        _ = ProcessFrame(state, output, input);
    }
    public void Dispose() => state.Dispose();

    private sealed class StateHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public StateHandle() : base(true) { }
        protected override bool ReleaseHandle() { Destroy(handle); return true; }
    }
    [DllImport("rnnoise", EntryPoint = "rnnoise_get_frame_size", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern int GetFrameSize();
    [DllImport("rnnoise", EntryPoint = "rnnoise_create", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern StateHandle Create(IntPtr model);
    [DllImport("rnnoise", EntryPoint = "rnnoise_destroy", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern void Destroy(IntPtr handle);
    [DllImport("rnnoise", EntryPoint = "rnnoise_process_frame", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern float ProcessFrame(StateHandle handle, [Out] float[] output, [In] float[] input);
}
