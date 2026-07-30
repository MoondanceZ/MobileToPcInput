using NUnit.Framework;
using pc_receiver;

namespace pc_receiver.Tests;

public sealed class AudioOutputDeviceSelectionTests
{
    [Test]
    public void Prefers_the_saved_endpoint_by_stable_name()
    {
        var devices = new[]
        {
            new AudioOutputDevice(1, "CABLE In 16ch", "endpoint-16ch"),
            new AudioOutputDevice(2, "CABLE Input", "endpoint-standard"),
        };

        var selected = AudioOutputService.SelectDevice(
            devices,
            "CABLE In 16ch");

        Assert.That(selected?.EndpointId, Is.EqualTo("endpoint-16ch"));
    }

    [Test]
    public void Defaults_to_standard_cable_input_instead_of_multichannel_alias()
    {
        var devices = new[]
        {
            new AudioOutputDevice(1, "CABLE In 16ch", "endpoint-16ch"),
            new AudioOutputDevice(
                2,
                "CABLE Input (VB-Audio Virtual Cable)",
                "endpoint-standard"),
        };

        var selected = AudioOutputService.SelectDevice(devices, null);

        Assert.That(selected?.EndpointId, Is.EqualTo("endpoint-standard"));
    }
}
