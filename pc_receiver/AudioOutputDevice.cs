namespace pc_receiver;

public sealed record AudioOutputDevice(int DeviceNumber, string Name)
{
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
