using System.Buffers.Binary;

namespace CommSpot.Client.Core;

/// <summary>Reads a JPEG's width and height from its header without decoding it, so a huge "image bomb"
/// from the network is refused before any pixel memory is allocated.</summary>
public static class JpegInfo
{
    public static bool TryReadSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                return false;
            }
            byte marker = jpeg[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2; // markers without a length
                continue;
            }
            if (marker is 0xD9 or 0xDA)
            {
                return false; // end of image or start of scan before any frame header
            }
            int len = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 2)..]);
            if (len < 2 || i + 2 + len > jpeg.Length)
            {
                return false;
            }
            // SOF0-SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (len < 7)
                {
                    return false;
                }
                height = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 7)..]);
                return width > 0 && height > 0;
            }
            i += 2 + len;
        }
        return false;
    }
}
