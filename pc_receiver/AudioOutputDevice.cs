namespace pc_receiver;

public sealed record AudioOutputDevice(
    int DeviceNumber,
    string Name,
    string EndpointId)
{
    public string DisplayName => Name;
    public string Description => IsLikelyVirtualCable
        ? "将手机语音转发到微信输入法"
        : "Windows 音频播放设备";
    public bool CanSelectInPicker => true;

    public bool IsLikelyVirtualCable
    {
        get
        {
            var name = Name.ToLowerInvariant();
            return name.Contains("cable")
                   || name.Contains("voicemeeter")
                   || name.Contains("virtual")
                   || name.Contains("vb-audio");
        }
    }

    public override string ToString()
    {
        return $"{DeviceNumber}: {Name}";
    }
}
