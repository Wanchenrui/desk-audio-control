internal sealed class SwitchGuard
{
    public readonly int MaxLevel;
    public readonly string Code, Reason;
    public bool Blocked { get { return MaxLevel < 0; } }

    SwitchGuard(int level,string code,string reason) { MaxLevel=level; Code=code; Reason=reason; }
    public static SwitchGuard Evaluate(SceneSignals s)
    {
        if (s.Unknown) return new SwitchGuard(-1,"usage_unknown","无法确认录音或通信占用"+(s.ReadError==null ? "" : " · "+s.ReadError));
        if (s.Camera) return new SwitchGuard(-1,"camera","摄像头正在使用");
        if (s.Communicating || s.Foreground=="会议") return new SwitchGuard(-1,"call","会议或通信输出正在使用");
        if (s.Capturing || s.Foreground=="录制") return new SwitchGuard(-1,"capture","录音正在进行");
        if (s.Away) return new SwitchGuard(-1,"away","离开期间保留当前设备");
        if (s.SharedBusy) return new SwitchGuard(1,"shared_playback","共用端口的播放用途不明，需要点击确认");
        return new SwitchGuard(3,null,null);
    }
}
