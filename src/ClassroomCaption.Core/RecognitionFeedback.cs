namespace ClassroomCaption.Core;

public static class RecognitionFeedback
{
    public static string Describe(double seconds, long capturedBytes, long sentBytes, double peakRms, bool receivedText)
    {
        if (receivedText) return "";
        if (seconds < 8) return "正在收音，等待俄语发言…";
        if (capturedBytes == 0) return "麦克风没有送来音频，请检查设备和麦克风权限。";
        if (peakRms < .0005) return "收到的音频接近静音，请换麦克风或先试听。";
        if (sentBytes == 0) return "已收到声音，但尚未发送到识别服务，请检查网络连接。";
        return "声音已发送，尚无识别结果，请试听麦克风。";
    }
}
