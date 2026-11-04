using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>MCS stream framing: <c>[version][tag][varint size][protobuf]</c>, then <c>[tag][varint size][protobuf]</c>.</summary>
[TestClass]
public class McsParserTests
{
    private static byte[] LoginResponseFrame(bool withVersion = true)
    {
        var body = new McsLoginResponse { Id = "chrome-63.0.3234.0", StreamId = 1 }.Encode();
        return McsPushClient.Frame(withVersion ? [41, 3] : [3], body);
    }

    [TestMethod]
    public void ReadsTheVersionByteOnlyOnTheFirstFrame()
    {
        var parser = new McsParser();
        var data = LoginResponseFrame().Concat(McsPushClient.BuildHeartbeatPing()).ToArray();

        var messages = parser.HandleData(data);

        Assert.HasCount(2, messages);
        Assert.AreEqual((int)McsMessageTag.LoginResponse, messages[0].Tag);
        Assert.AreEqual("chrome-63.0.3234.0", ((McsLoginResponse)messages[0].Message!).Id);
        Assert.AreEqual((int)McsMessageTag.HeartbeatPing, messages[1].Tag);
        Assert.IsInstanceOfType<McsHeartbeat>(messages[1].Message);
    }

    [TestMethod]
    public void RejectsAProtocolVersionBelow38()
    {
        var parser = new McsParser();
        Assert.ThrowsExactly<InvalidDataException>(() => parser.HandleData([37, 3, 0]));
    }

    [TestMethod]
    public void WaitsForTheFirstThreeBytesBeforeReadingTheVersion()
    {
        var parser = new McsParser();
        Assert.IsEmpty(parser.HandleData([41]));
        Assert.IsEmpty(parser.HandleData([0]));
        // A zero-size HeartbeatPing completes with the third byte.
        var messages = parser.HandleData([0]);
        Assert.HasCount(1, messages);
        Assert.AreEqual((int)McsMessageTag.HeartbeatPing, messages[0].Tag);
    }

    [TestMethod]
    public void ReassemblesAStreamSplitAtEveryByte()
    {
        var stanza = new McsDataMessageStanza
        {
            From = "348804314802",
            Category = "com.oceanwing.battery.cam",
            PersistentId = "0:1",
            // > 127 bytes, so the size takes a two-byte varint that is split too.
            RawData = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(),
        };
        var data = LoginResponseFrame()
            .Concat(McsPushClient.Frame([(byte)McsMessageTag.DataMessageStanza], stanza.Encode()))
            .Concat(McsPushClient.Frame([(byte)McsMessageTag.Close], []))
            .ToArray();

        var parser = new McsParser();
        var messages = new List<McsMessage>();
        foreach (var b in data) messages.AddRange(parser.HandleData([b]));

        CollectionAssert.AreEqual(
            new[] { (int)McsMessageTag.LoginResponse, (int)McsMessageTag.DataMessageStanza, (int)McsMessageTag.Close },
            messages.Select(m => m.Tag).ToArray());
        var decoded = (McsDataMessageStanza)messages[1].Message!;
        Assert.AreEqual("0:1", decoded.PersistentId);
        CollectionAssert.AreEqual(stanza.RawData, decoded.RawData);
    }

    [TestMethod]
    public void HandlesSeveralMessagesInOneChunkAndAnUnknownTag()
    {
        var parser = new McsParser();
        var data = LoginResponseFrame()
            .Concat(McsPushClient.Frame([(byte)McsMessageTag.TalkMetadata], [0x08, 0x01]))
            .Concat(McsPushClient.BuildHeartbeatAck(7))
            .ToArray();

        var messages = parser.HandleData(data);

        Assert.HasCount(3, messages);
        Assert.AreEqual((int)McsMessageTag.TalkMetadata, messages[1].Tag);
        Assert.IsNull(messages[1].Message);
        Assert.AreEqual(7, ((McsHeartbeat)messages[2].Message!).LastStreamIdReceived);
    }

    [TestMethod]
    public void ResetStartsANewStreamWithAVersionByte()
    {
        var parser = new McsParser();
        parser.HandleData(LoginResponseFrame());
        parser.Reset();
        var messages = parser.HandleData(LoginResponseFrame());
        Assert.HasCount(1, messages);
        Assert.AreEqual((int)McsMessageTag.LoginResponse, messages[0].Tag);
    }

    [TestMethod]
    public void RejectsAnOversizedLength()
    {
        var parser = new McsParser();
        Assert.ThrowsExactly<InvalidDataException>(() => parser.HandleData([41, 8, 0xFF, 0xFF, 0xFF, 0xFF, 0x07]));
    }
}
