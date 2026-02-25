namespace LocalhostMiniOTG.Services;

/// <summary>
/// Extracts embedded JPEG previews from camera RAW files (CR2, NEF, ARW, DNG).
/// Most RAW formats are based on TIFF and contain one or more embedded JPEG images
/// (thumbnail + full-size preview). This class scans for JPEG markers (FFD8/FFD9)
/// and returns the largest DISPLAYABLE one found.
///
/// CR2 files also contain RAW sensor data encoded as lossless JPEG (SOF3 / FF C3)
/// which is the largest "JPEG" but cannot be rendered by browsers. We skip those.
/// No external tools (FFmpeg, dcraw, etc.) are needed.
/// </summary>
public static class RawJpegExtractor
{
    private static readonly byte[] JpegStart = { 0xFF, 0xD8, 0xFF };

    /// <summary>
    /// Reads a RAW file and returns the largest displayable embedded JPEG, or null if none found.
    /// </summary>
    public static byte[]? ExtractLargestJpeg(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            return ExtractLargestDisplayableJpeg(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAW] ExtractLargestJpeg error: {ex.Message}");
            return null;
        }
    }

    private static byte[]? ExtractLargestDisplayableJpeg(byte[] data)
    {
        var candidates = new List<(int start, int end)>();
        int i = 0;

        // Find all JPEG streams in the file
        while (i < data.Length - 3)
        {
            int start = FindPattern(data, JpegStart, i);
            if (start < 0) break;

            int end = FindJpegEnd(data, start + 3);
            if (end < 0) { i = start + 3; continue; }

            int length = end + 2 - start;
            if (length > 1024) // skip tiny fragments
                candidates.Add((start, end));

            i = end + 2;
        }

        Console.WriteLine($"  [RAW] Found {candidates.Count} embedded JPEG(s)");

        // Sort by size descending, pick the largest displayable one
        candidates.Sort((a, b) => (b.end - b.start).CompareTo(a.end - a.start));

        foreach (var (start, end) in candidates)
        {
            int length = end + 2 - start;

            // Check if this JPEG is displayable (not lossless RAW sensor data)
            if (IsLosslessJpeg(data, start, end))
            {
                Console.WriteLine($"  [RAW]   Skipping lossless JPEG ({length / 1024} KB) — RAW sensor data");
                continue;
            }

            Console.WriteLine($"  [RAW]   Found displayable JPEG ({length / 1024} KB)");
            var jpeg = new byte[length];
            Array.Copy(data, start, jpeg, 0, length);
            return jpeg;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the JPEG uses lossless compression (SOF3 = FF C3),
    /// which is used for RAW sensor data and cannot be displayed by browsers.
    /// </summary>
    private static bool IsLosslessJpeg(byte[] data, int jpegStart, int jpegEnd)
    {
        // Scan the first few KB of the JPEG for SOF markers
        int scanLimit = Math.Min(jpegStart + 4096, jpegEnd);
        for (int i = jpegStart + 2; i < scanLimit - 1; i++)
        {
            if (data[i] != 0xFF) continue;

            byte marker = data[i + 1];

            // SOF3 (FF C3) = lossless JPEG — this is RAW sensor data
            if (marker == 0xC3) return true;

            // SOF0 (FF C0) = baseline, SOF1 (FF C1) = extended, SOF2 (FF C2) = progressive
            // These are all displayable — not lossless
            if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2) return false;
        }

        // No SOF found in first 4KB — assume it might be lossless, skip it
        // (normal displayable JPEGs always have SOF within the first few hundred bytes)
        return true;
    }

    private static int FindPattern(byte[] data, byte[] pattern, int startIndex)
    {
        int end = data.Length - pattern.Length;
        for (int i = startIndex; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the end of a JPEG (FF D9) by scanning forward.
    /// Returns when hitting the next FF D8 (new JPEG start) or end of data.
    /// </summary>
    private static int FindJpegEnd(byte[] data, int startIndex)
    {
        int lastEnd = -1;

        for (int i = startIndex; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                lastEnd = i;
            }
            else if (data[i] == 0xFF && data[i + 1] == 0xD8 && i > startIndex + 100)
            {
                // Hit a new JPEG start — the previous FF D9 was the end of our JPEG
                if (lastEnd >= 0) return lastEnd;
            }
        }

        return lastEnd;
    }
}
