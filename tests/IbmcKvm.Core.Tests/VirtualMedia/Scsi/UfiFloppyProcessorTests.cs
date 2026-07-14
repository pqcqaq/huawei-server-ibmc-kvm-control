using System.Buffers.Binary;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Core.VirtualMedia.Scsi;

namespace IbmcKvm.Core.Tests.VirtualMedia.Scsi;

public sealed class UfiFloppyProcessorTests
{
    [Fact]
    public async Task MatchesInquiryCapacityAndSenseFormats()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Floppy, 512, 2880);
        var processor = new UfiFloppyProcessor(media);

        var inquiry = await processor.ProcessAsync(Command(0x12));
        Assert.True(inquiry.Success);
        Assert.Equal("008000011F0000005669727475616C20464C4F50505920564D20312E312E302020202020", Convert.ToHexString(inquiry.Data));

        var changed = await processor.ProcessAsync(Command(0x00));
        Assert.False(changed.Success);
        Assert.Equal(new ScsiSense(6, 0x28, 0), changed.Sense);
        var sense = await processor.ProcessAsync(Command(0x03));
        Assert.True(sense.Success);
        Assert.Equal("700006000000000A00000000280000000000", Convert.ToHexString(sense.Data));

        var capacity = await processor.ProcessAsync(Command(0x25));
        Assert.True(capacity.Success);
        Assert.Equal("00000B3F00000200", Convert.ToHexString(capacity.Data));
        var formats = await processor.ProcessAsync(Command(0x23));
        Assert.True(formats.Success);
        Assert.Equal(20, formats.Data.Length);
        Assert.Equal(2880u, BinaryPrimitives.ReadUInt32BigEndian(formats.Data.AsSpan(4, 4)));
    }

    [Fact]
    public async Task ReadsAndWritesAllOriginalTransferVariants()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Floppy, 512, 64);
        var processor = new UfiFloppyProcessor(media);
        await ConsumeUnitAttentionAsync(processor);

        foreach (var opcode in new byte[] { 0x28, 0xA8 })
        {
            var response = await processor.ProcessAsync(BlockCommand(opcode, 2, 2));
            Assert.True(response.Success);
            Assert.Equal(media.Content.Slice(1024, 1024).ToArray(), response.Data);
        }

        foreach (var opcode in new byte[] { 0x2A, 0x2E, 0xAA })
        {
            var command = BlockCommand(opcode, 4, 1);
            var data = Enumerable.Repeat(opcode, 512).ToArray();
            Assert.Equal(512, processor.GetExpectedDataOutLength(command));
            var response = await processor.ProcessAsync(command, data);
            Assert.True(response.Success);
            Assert.Equal(data, media.Content.Slice(2048, 512).ToArray());
        }
    }

    [Fact]
    public async Task ImplementsModeFormatAndSimpleOriginalCommands()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Floppy, 512, 32);
        var processor = new UfiFloppyProcessor(media);
        await ConsumeUnitAttentionAsync(processor);

        foreach (var opcode in new byte[] { 0x01, 0x1D, 0x2B, 0x2F })
        {
            Assert.True((await processor.ProcessAsync(Command(opcode))).Success);
        }

        Assert.True((await processor.ProcessAsync(Command(0x1B))).Success);
        Assert.True((await processor.ProcessAsync(Command(0x1E))).Success);

        var modeSense = Command(0x5A);
        modeSense[2] = 0x3F;
        BinaryPrimitives.WriteUInt16BigEndian(modeSense.AsSpan(7, 2), 128);
        var mode = await processor.ProcessAsync(modeSense);
        Assert.True(mode.Success);
        Assert.Equal(0x94, mode.Data[2]);
        Assert.Contains((byte)0x1C, mode.Data);

        var modeSelect = Command(0x55);
        BinaryPrimitives.WriteUInt16BigEndian(modeSelect.AsSpan(7, 2), 4);
        Assert.Equal(4, processor.GetExpectedDataOutLength(modeSelect));
        Assert.True((await processor.ProcessAsync(modeSelect, new byte[4])).Success);

        var format = Command(0x04);
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(7, 2), 12);
        Assert.True((await processor.ProcessAsync(format, new byte[12])).Success);
        Assert.All(media.Content.ToArray(), static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReportsWriteProtectionBoundsAndUnsupportedCommands()
    {
        var media = new ScsiTestMedia(MediaDeviceKind.Floppy, 512, 8, readOnly: true);
        var processor = new UfiFloppyProcessor(media);
        await ConsumeUnitAttentionAsync(processor);

        var write = await processor.ProcessAsync(BlockCommand(0x2A, 0, 1), new byte[512]);
        Assert.False(write.Success);
        Assert.Equal(new ScsiSense(7, 0x27, 0), write.Sense);

        var read = await processor.ProcessAsync(BlockCommand(0x28, 8, 1));
        Assert.False(read.Success);
        Assert.Equal(new ScsiSense(5, 0x21, 0), read.Sense);

        var unsupported = await processor.ProcessAsync(Command(0xFF));
        Assert.False(unsupported.Success);
        Assert.Equal(new ScsiSense(5, 0x24, 0), unsupported.Sense);
    }

    private static async Task ConsumeUnitAttentionAsync(UfiFloppyProcessor processor)
    {
        var changed = await processor.ProcessAsync(Command(0x00));
        Assert.Equal(new ScsiSense(6, 0x28, 0), changed.Sense);
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
        if (opcode is 0xA8 or 0xAA)
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
