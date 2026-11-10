namespace ClassroomCaption.App;

public enum SubtitleActivity { Ready, Connecting, Buffering, Listening, Demo, Stopping, Stopped, Reconnecting, RecoveringAudio, Faulted }
public static class SubtitleActivityText
{
    public static string Empty(SubtitleActivity activity) => activity switch
    {
        SubtitleActivity.Connecting => "正在连接服务…",
        SubtitleActivity.Buffering => "正在收音，连接成功后自动补发…",
        SubtitleActivity.Reconnecting => "连接中断，正在自动恢复…",
        SubtitleActivity.RecoveringAudio => "麦克风中断，正在自动恢复…",
        SubtitleActivity.Faulted => "翻译因错误停止",
        SubtitleActivity.Listening => "等待俄语发言…",
        SubtitleActivity.Demo => "本地字幕演示",
        SubtitleActivity.Stopping => "正在结束并保存…",
        SubtitleActivity.Stopped => "本次翻译已停止",
        _ => "尚未开始翻译"
    };
    public static string Label(SubtitleActivity activity) => activity switch
    {
        SubtitleActivity.Connecting => "连接中",
        SubtitleActivity.Buffering => "收音中 · 连接中",
        SubtitleActivity.Reconnecting => "正在自动恢复",
        SubtitleActivity.RecoveringAudio => "正在恢复麦克风",
        SubtitleActivity.Faulted => "错误中断",
        SubtitleActivity.Listening => "收音中",
        SubtitleActivity.Demo => "本地演示",
        SubtitleActivity.Stopping => "正在停止",
        SubtitleActivity.Stopped => "已停止",
        _ => "未开始"
    };
}
