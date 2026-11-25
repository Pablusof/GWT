namespace GwtEmulator.Protocol;

public static class CryptoHelper
{
    public static byte[] GenerateKey(long guid, int certification)
    {
        return new byte[8]
        {
            (byte)certification,
            (byte)(certification >> 8),
            (byte)(certification >> 16),
            (byte)(certification >> 24),
            (byte)guid,
            (byte)(guid >> 8),
            (byte)(guid >> 16),
            (byte)(guid >> 24)
        };
    }

    public static void Encrypt(byte[] data, byte[] key, int start, int end)
    {
        // The client's TKDecrypt (and this class's own Decrypt) index the payload RELATIVE to
        // its start (i = 0..len). Encrypt must mirror that: use the offset relative to `start`,
        // not the absolute buffer index. Using the absolute index shifted every byte by `start`
        // (the 9-byte header), so the client decrypted every room packet to garbage -> a huge
        // PlayerList/TankBattleData count -> IndexOutOfRange that killed the room receive thread
        // (battle stuck on the loading screen).
        int num = 0;
        for (int i = start; i < end; i++)
        {
            if (num > 7) num = 0;
            data[i] = (byte)(data[i] + key[num++] - (i - start));
        }
    }

    public static void Decrypt(byte[] data, int len, byte[] key)
    {
        int num = 0;
        for (int i = 0; i < len; i++)
        {
            if (num > 7) num = 0;
            data[i] = (byte)(data[i] - key[num++] + i);
        }
    }

    public static ushort ComputeCheckCode(byte[] data, int start, int end)
    {
        ushort checksum = 0;
        for (int i = start; i < end; i++)
        {
            checksum = (ushort)((checksum + i + 1) * data[i]);
        }
        return (ushort)(checksum ^ 0x1234);
    }
}
