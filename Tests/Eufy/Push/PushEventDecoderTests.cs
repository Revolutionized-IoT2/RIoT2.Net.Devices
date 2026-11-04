using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>Push event → semantic event, ported from upstream's decode-event spec (push source) for the supported kinds.</summary>
[TestClass]
public class PushEventDecoderTests
{
    private static EufySecurityEvent? Decode(int eventType, string payloadJson = "{}", string? thumbnail = null)
    {
        var payload = JsonNode.Parse(payloadJson)!.AsObject();
        payload["event_type"] = eventType;
        if (thumbnail is not null) payload["pic_url"] = thumbnail;
        var envelope = new JsonObject { ["device_sn"] = "D1", ["station_sn"] = "S1", ["payload"] = payload };
        return PushEventDecoder.Decode(PushNormalizer.Normalize(new RawPushMessage { Payload = envelope }));
    }

    [TestMethod]
    [DataRow(3101, EufyEventKind.Motion)]
    [DataRow(14, EufyEventKind.Motion)]
    [DataRow(3102, EufyEventKind.PersonDetected)]
    [DataRow(3111, EufyEventKind.PersonDetected)]
    [DataRow(3112, EufyEventKind.StrangerDetected)]
    [DataRow(3106, EufyEventKind.PetDetected)]
    [DataRow(3108, EufyEventKind.DogDetected)]
    [DataRow(3109, EufyEventKind.DogDetected)]
    [DataRow(3110, EufyEventKind.DogDetected)]
    [DataRow(3105, EufyEventKind.SoundDetected)]
    [DataRow(3104, EufyEventKind.CryingDetected)]
    [DataRow(3107, EufyEventKind.VehicleDetected)]
    [DataRow(3103, EufyEventKind.DoorbellPress)]
    [DataRow(3301, EufyEventKind.PackageDelivered)]
    [DataRow(3302, EufyEventKind.PackageTaken)]
    [DataRow(3304, EufyEventKind.PackageStranded)]
    [DataRow(9, EufyEventKind.ArmingModeChanged)]
    [DataRow(10, EufyEventKind.Alarm)]
    [DataRow(16, EufyEventKind.Alarm)]
    public void MapsThePushIdToItsSemanticEvent(int eventType, EufyEventKind kind)
    {
        var e = Decode(eventType);
        Assert.IsNotNull(e);
        Assert.AreEqual(kind, e.Kind);
        Assert.AreEqual("D1", e.DeviceSerial);
        Assert.AreEqual("S1", e.StationSerial);
        Assert.AreEqual(eventType, e.EventType);
    }

    [TestMethod]
    [DataRow(9999)]
    [DataRow(3)] // contactState: not a supported kind
    [DataRow(6)] // batteryAlert
    [DataRow(257)] // lockState
    [DataRow(3303)] // FAMILY_DETECTION: no capability maps it
    public void AnUnmappedOrUnsupportedIdYieldsNoEvent(int eventType) => Assert.IsNull(Decode(eventType));

    [TestMethod]
    public void APushWithoutANumericTypeYieldsNoEvent()
    {
        var envelope = new JsonObject { ["payload"] = new JsonObject { ["event_type"] = "3101" } };
        Assert.IsNull(PushEventDecoder.Decode(PushNormalizer.Normalize(new RawPushMessage { Payload = envelope })));
    }

    [TestMethod]
    public void CarriesTheThumbnailThrough()
    {
        Assert.AreEqual("http://x/y.jpg", Decode(3101, thumbnail: "http://x/y.jpg")!.ThumbnailUrl);
        Assert.IsNull(Decode(3101)!.ThumbnailUrl);
    }

    [TestMethod]
    public void ReportsTheAlarmLifecycle()
    {
        Assert.AreEqual(EufyAlarmPhase.Triggered, Decode(10)!.AlarmPhase);
        Assert.AreEqual(EufyAlarmPhase.Delayed, Decode(16)!.AlarmPhase);
        Assert.IsNull(Decode(3101)!.AlarmPhase);
    }

    /// <summary>Shapes from a HomeBase trigger then stop: <c>type: 4</c> (APP) then <c>type: 16</c> (HUB_STOP_BY_APP).</summary>
    [TestMethod]
    public void ReadsTheAlarmPhaseFromThePushsAlarmType()
    {
        var triggered = Decode(10, "{\"type\":4}")!;
        Assert.AreEqual(EufyAlarmPhase.Triggered, triggered.AlarmPhase);
        Assert.AreEqual(4, triggered.AlarmType);

        var stopped = Decode(10, "{\"type\":16}")!;
        Assert.AreEqual(EufyAlarmPhase.Stopped, stopped.AlarmPhase);
        Assert.AreEqual(16, stopped.AlarmType);

        var bare = Decode(10)!;
        Assert.AreEqual(EufyAlarmPhase.Triggered, bare.AlarmPhase);
        Assert.IsNull(bare.AlarmType);

        Assert.AreEqual(3, Decode(10, "{\"alarm_type\":3}")!.AlarmType);
        // A string type is not the stop code, as upstream's strict comparison.
        Assert.AreEqual(EufyAlarmPhase.Triggered, Decode(10, "{\"type\":\"16\"}")!.AlarmPhase);
    }

    [TestMethod]
    public void ThePhaseOfAnAlarmDelayIsNotOverwrittenByTheBody()
    {
        Assert.AreEqual(EufyAlarmPhase.Delayed, Decode(16, "{\"type\":16}")!.AlarmPhase);
    }

    [TestMethod]
    public void NamesARecognisedPerson()
    {
        Assert.AreEqual("Alice,Bob", Decode(3111, "{\"objects\":{\"names\":[\"Alice\",\"Bob\"]}}")!.PersonName);
        Assert.AreEqual("Alice", Decode(3102, "{\"person_name\":\"Alice\"}")!.PersonName);
        Assert.IsNull(Decode(3102)!.PersonName);
        Assert.IsNull(Decode(3102, "{\"objects\":[\"person\"]}")!.PersonName);
        Assert.IsNull(Decode(3112, "{\"objects\":{\"names\":[\"Alice\"]}}")!.PersonName);
    }

    [TestMethod]
    public void ResolvesDetectionNames()
    {
        Assert.AreEqual("MOTION_DETECTION", PushEvents.DetectionName(3101));
        Assert.AreEqual("PACKAGE_DELIVERED", PushEvents.DetectionName(3301));
        Assert.AreEqual("STRANGER_PERSON_DETECTION", PushEvents.DetectionName(3112));
        Assert.AreEqual("EVENT_10", PushEvents.DetectionName(10));
    }
}
