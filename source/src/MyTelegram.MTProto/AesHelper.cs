using System.Numerics;

namespace MyTelegram.MTProto;

public class AesHelper : IAesHelper, ITransientDependency
{
    public void CtrEncrypt(ReadOnlySpan<byte> input, Span<byte> output, byte[] key, byte[] iv,
        ulong offset = 0)
    {
        if (input.Length != output.Length)
        {
            throw new ArgumentException("Input and output must be the same length.");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        const int blockSize = 16;
        const int blocksPerBatch = 32;
        const int batchSize = blockSize * blocksPerBatch;

        Span<byte> counter = stackalloc byte[blockSize];
        iv.CopyTo(counter);
        AddToCounter(counter, offset / blockSize);

        var counterBatch = ArrayPool<byte>.Shared.Rent(batchSize);
        var encryptedBytes = ArrayPool<byte>.Shared.Rent(batchSize);
        var encryptedSpan = encryptedBytes.AsSpan();
        var counterBatchSpan = counterBatch.AsSpan();

        var inputOffset = 0;
        var blockOffset = (int)(offset % blockSize);
        var length = input.Length;

        Span<byte> tempCounter = stackalloc byte[blockSize];
        counter.CopyTo(tempCounter);
        try
        {
            while (inputOffset < length)
            {
                var remaining = length - inputOffset;
                var blocksToGen = Math.Min(blocksPerBatch, (remaining + blockOffset + blockSize - 1) / blockSize);
                var bytesToXor = Math.Min(remaining, blocksToGen * blockSize - blockOffset);

                for (var i = 0; i < blocksToGen; i++)
                {
                    tempCounter.CopyTo(counterBatchSpan.Slice(i * blockSize, blockSize));
                    IncrementCounter(tempCounter);
                }

                encryptor.TransformBlock(counterBatch, 0, blocksToGen * blockSize, encryptedBytes, 0);

                var keyStreamSpan = encryptedSpan.Slice(blockOffset, bytesToXor);
                var inputSpan = input.Slice(inputOffset, bytesToXor);
                var outputSpan = output.Slice(inputOffset, bytesToXor);

                if (Vector.IsHardwareAccelerated && bytesToXor >= Vector<byte>.Count)
                {
                    var simdCount = bytesToXor - bytesToXor % Vector<byte>.Count;
                    for (var i = 0; i < simdCount; i += Vector<byte>.Count)
                    {
                        var vInput = new Vector<byte>(inputSpan[i..]);
                        var vKey = new Vector<byte>(keyStreamSpan[i..]);
                        (vInput ^ vKey).CopyTo(outputSpan[i..]);
                    }

                    for (var i = simdCount; i < bytesToXor; i++)
                    {
                        outputSpan[i] = (byte)(inputSpan[i] ^ keyStreamSpan[i]);
                    }
                }
                else
                {
                    for (var i = 0; i < bytesToXor; i++)
                    {
                        outputSpan[i] = (byte)(inputSpan[i] ^ keyStreamSpan[i]);
                    }
                }

                inputOffset += bytesToXor;
                blockOffset = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counterBatch);
            ArrayPool<byte>.Shared.Return(encryptedBytes);
        }
    }

    //public void CtrEncrypt(ReadOnlyMemory<byte> input, Memory<byte> output, byte[] key, byte[] iv, long offset = 0)
    //{
    //    CtrEncrypt(input.Span, output.Span, key, iv, offset);
    //}

    private static void IncrementCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    private static void AddToCounter(Span<byte> counter, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            var index = counter.Length - 1 - i;
            value += counter[index];
            counter[index] = (byte)value;
            value >>= 8;
        }
    }
}
