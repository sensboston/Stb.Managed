using System.Text;
using Stb.Managed.Internal;
using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class ChecksumTests
{
    private static readonly int[] BoundaryLengths =
    [
        0, 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65,
        79, 80, 81, 127, 128, 129, 255, 256, 513, 5536, 5551, 5552, 5553, 12000,
    ];

    private static readonly uint[] SeedCrcs = [0xFFFFFFFFu, 0x00000000u, 0x12345678u, 0xDEADBEEFu];
    private static readonly uint[] SeedAdlers = [0x00000001u, 0x12345678u, 0xFFF0FFF0u];

    [Fact]
    public void Crc32MatchesStandardVectors()
    {
        Assert.Equal(0x00000000u, Checksums.ComputeCrc32(ReadOnlySpan<byte>.Empty));

        byte[] check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Checksums.ComputeCrc32(check));

        // PNG IEND chunk type ("IEND") with empty payload has well-known CRC 0xAE426082.
        byte[] iend = Encoding.ASCII.GetBytes("IEND");
        Assert.Equal(0xAE426082u, Checksums.ComputeCrc32(iend, ReadOnlySpan<byte>.Empty));

        // Split header + payload must match contiguous computation.
        Assert.Equal(
            Checksums.ComputeCrc32(check),
            Checksums.ComputeCrc32(check.AsSpan(0, 4), check.AsSpan(4)));
    }

    [Fact]
    public void Crc32SimdMatchesScalarAcrossLengthsAlignmentsAndSeeds()
    {
        byte[] data = new byte[12016];
        uint state = 0xA1B2C3D4u;
        for (int i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)state;
        }

        foreach (int len in BoundaryLengths)
        {
            foreach (uint seed in SeedCrcs)
            {
                ReadOnlySpan<byte> slice = data.AsSpan(0, len);
                uint expected = Checksums.UpdateCrc32Scalar(seed, slice);
                uint actual = Checksums.UpdateCrc32(seed, slice);
                Assert.Equal(expected, actual);
            }
        }

        // Unaligned offsets 0..15 across 257 bytes.
        for (int offset = 0; offset < 16; offset++)
        {
            ReadOnlySpan<byte> slice = data.AsSpan(offset, 257);
            Assert.Equal(
                Checksums.UpdateCrc32Scalar(0xFFFFFFFFu, slice),
                Checksums.UpdateCrc32(0xFFFFFFFFu, slice));
        }
    }

    [Fact]
    public void Adler32MatchesStandardVectorsAndLargeBuffers()
    {
        Assert.Equal(0x00000001u, Checksums.ComputeAdler32(ReadOnlySpan<byte>.Empty));

        byte[] wiki = Encoding.ASCII.GetBytes("Wikipedia");
        Assert.Equal(0x11E60398u, Checksums.ComputeAdler32(wiki));

        // Cross multiple 5552-byte NMAX modulo blocks and verify against simple per-byte reference.
        byte[] large = new byte[20_000];
        for (int i = 0; i < large.Length; i++)
        {
            large[i] = (byte)((i * 73 + 19) & 0xFF);
        }

        uint expected = ReferenceAdler32(large);
        Assert.Equal(expected, Checksums.ComputeAdler32(large));
        Assert.Equal(expected, Checksums.UpdateAdler32Scalar(1u, large));

        // Streaming update in odd chunks must match single-pass.
        uint running = 1u;
        int offset = 0;
        int[] chunkSizes = [1, 17, 5551, 5552, 5553, 3326];
        foreach (int size in chunkSizes)
        {
            running = Checksums.UpdateAdler32(running, large.AsSpan(offset, size));
            offset += size;
        }

        Assert.Equal(large.Length, offset);
        Assert.Equal(expected, running);
    }

    [Fact]
    public void Adler32SimdMatchesScalarAcrossLengthsOverflowStressAndAlignments()
    {
        byte[] randomData = new byte[12016];
        uint state = 0x55667788u;
        for (int i = 0; i < randomData.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            randomData[i] = (byte)state;
        }

        // Maximum-byte-value buffer stresses NMAX accumulation against 32-bit overflow.
        byte[] maxBytes = new byte[20_000];
        Array.Fill(maxBytes, (byte)0xFF);

        foreach (int len in BoundaryLengths)
        {
            foreach (uint seed in SeedAdlers)
            {
                ReadOnlySpan<byte> slice = randomData.AsSpan(0, len);
                Assert.Equal(
                    Checksums.UpdateAdler32Scalar(seed, slice),
                    Checksums.UpdateAdler32(seed, slice));

                ReadOnlySpan<byte> maxSlice = maxBytes.AsSpan(0, len);
                Assert.Equal(
                    Checksums.UpdateAdler32Scalar(seed, maxSlice),
                    Checksums.UpdateAdler32(seed, maxSlice));
            }
        }

        Assert.Equal(ReferenceAdler32(maxBytes), Checksums.ComputeAdler32(maxBytes));

        // Unaligned offsets 0..15.
        for (int offset = 0; offset < 16; offset++)
        {
            ReadOnlySpan<byte> slice = randomData.AsSpan(offset, 5569);
            Assert.Equal(
                Checksums.UpdateAdler32Scalar(1u, slice),
                Checksums.UpdateAdler32(1u, slice));
        }
    }

    private static uint ReferenceAdler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte v in data)
        {
            a = (a + v) % 65521u;
            b = (b + a) % 65521u;
        }

        return (b << 16) | a;
    }
}
