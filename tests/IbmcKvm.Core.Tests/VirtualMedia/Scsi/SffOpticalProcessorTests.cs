using System.Buffers.Binary;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Core.VirtualMedia.Scsi;

namespace IbmcKvm.Core.Tests.VirtualMedia.Scsi;

public sealed class SffOpticalProcessorTests
{
    [Fact]
    public async Task MatchesInquiryCapacityReadAndTocBehavior()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Optical, 2048, 100);
        var processor = new SffOpticalProcessor(media);

        var inquiry = await processor.ProcessAsync(Command(0x12));
        Assert.True(inquiry.Success);
        Assert.Equal("058000211F0000005669727475616C204456442D524F4D20564D20312E312E3020323235", Convert.ToHexString(inquiry.Data));
        await ConsumeUnitAttentionAsync(processor);

        var capacity = await processor.ProcessAsync(Command(0x25));
        Assert.True(capacity.Success);
        Assert.Equal("0000006300000800", Convert.ToHexString(capacity.Data));

        foreach (var opcode in new byte[] { 0x28, 0xA8 })
        {
            var response = await processor.ProcessAsync(BlockCommand(opcode, 2, 2));
            Assert.True(response.Success);
            Assert.Equal(media.Content.Slice(4096, 4096).ToArray(), response.Data);
        }

        var tocCommand = Command(0x43);
        BinaryPrimitives.WriteUInt16BigEndian(tocCommand.AsSpan(7, 2), 32);
        var toc = await processor.ProcessAsync(tocCommand);
        Assert.True(toc.Success);
        Assert.Equal(20, toc.Data.Length);
        Assert.Equal(1, toc.Data[2]);
        Assert.Equal(0xAA, toc.Data[14]);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(toc.Data.AsSpan(16, 4)));
    }

    [Fact]
    public async Task ImplementsModeLifecycleAndSimpleProtocolCommands()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Optical, 2048, 16);
        var processor = new SffOpticalProcessor(media);
        await ConsumeUnitAttentionAsync(processor);

        Assert.True((await processor.ProcessAsync(Command(0x1E))).Success);
        Assert.True((await processor.ProcessAsync(Command(0x2B))).Success);

        var modeSense = Command(0x5A);
        BinaryPrimitives.WriteUInt16BigEndian(modeSense.AsSpan(7, 2), 8);
        var mode = await processor.ProcessAsync(modeSense);
        Assert.True(mode.Success);
        Assert.Equal(new byte[] { 0, 6, 0x70, 0, 0, 0, 0, 0 }, mode.Data);

        var modeSelect = Command(0x55);
        BinaryPrimitives.WriteUInt16BigEndian(modeSelect.AsSpan(7, 2), 6);
        Assert.True((await processor.ProcessAsync(modeSelect, new byte[6])).Success);

        var ejectCommand = Command(0x1B);
        ejectCommand[4] = 2;
        var eject = await processor.ProcessAsync(ejectCommand);
        Assert.True(eject.Success);
        Assert.Equal(ScsiMediaAction.Eject, eject.MediaAction);
        Assert.Equal(new ScsiSense(2, 0x3A, 0), (await processor.ProcessAsync(Command(0x00))).Sense);

        ejectCommand[4] = 3;
        var load = await processor.ProcessAsync(ejectCommand);
        Assert.True(load.Success);
        Assert.Equal(ScsiMediaAction.Load, load.MediaAction);
        Assert.Equal(new ScsiSense(6, 0x28, 0), (await processor.ProcessAsync(Command(0x00))).Sense);
        Assert.True((await processor.ProcessAsync(Command(0x00))).Success);
    }

    [Theory]
    [InlineData(0x42)]
    [InlineData(0x44)]
    [InlineData(0xB9)]
    [InlineData(0xBE)]
    [InlineData(0x4E)]
    [InlineData(0x37)]
    [InlineData(0xFF)]
    public async Task ReturnsInvalidFieldForUnsupportedOpticalCommands(byte opcode)
    {
        var processor = new SffOpticalProcessor(new ScsiTestMedia(MediaDeviceKind.Optical, 2048, 8));
        await ConsumeUnitAttentionAsync(processor);

        var response = await processor.ProcessAsync(Command(opcode));

        Assert.False(response.Success);
        Assert.Equal(new ScsiSense(5, 0x24, 0), response.Sense);
    }

    [Fact]
    public async Task ExperimentalReadyCommandCompletesWithFailureLikeLegacyClient()
    {
        var processor = new SffOpticalProcessor(new ScsiTestMedia(MediaDeviceKind.Optical, 2048, 8));
        await ConsumeUnitAttentionAsync(processor);

        var response = await processor.ProcessAsync(Command(0x4A));

        Assert.False(response.Success);
        Assert.Equal(ScsiSense.None, response.Sense);
    }

    private static async Task ConsumeUnitAttentionAsync(SffOpticalProcessor processor)
    {
        Assert.Equal(new ScsiSense(6, 0x28, 0), (await processor.ProcessAsync(Command(0x00))).Sense);
        Assert.True((await processor.ProcessAsync(Command(0x00))).Success);
    }

    private static byte[] Command(byte opcode)
    {
        var result = new byte[12];
        result[0] = opcode;
        return result;
    }

    private static byte[] BlockCommand(byte opcode, uint lba, uint blocks)
    {
        var result = Command(opcode);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(2, 4), lba);
        if (opcode == 0xA8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(6, 4), blocks);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(7, 2), checked((ushort)blocks));
        }

        return result;
    }
}
