/* Scalar Y4M display subset adapted from rigaya/HWEncVppPreview
 * 1faa89bd73cbce94788192f4ceaf5e061119bd52 (Y4m, YuvToBgra, HdrToneMapping).
The MIT License (MIT)

Copyright (c) 2026 rigaya

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FilmGrainStudioPreview
{
internal static class TrueHdrPreviewFrames
{
    internal const int ReferenceWhite = 203;
    private const float Knee = 0.75f;
    private static readonly float[] Pq = MakeTable(16384, PqToNits);
    private static readonly float[] Gamma = MakeTable(8192, x => 255 * Math.Pow(x * x, 1 / 2.4));

    internal static Bitmap Read(string path, MediaProbeInfo source, bool hdr, int peak, CancellationToken token)
    {
        using (var stream = File.OpenRead(path))
            return Read(stream, source, hdr, peak, token);
    }

    internal static Bitmap Read(Stream stream, MediaProbeInfo source, bool hdr, int peak, CancellationToken token)
    {
        string header = ReadLine(stream);
        if (!header.StartsWith("YUV4MPEG2 ", StringComparison.Ordinal)) throw new InvalidDataException("Invalid Y4M header.");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in header.Substring(10).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = field[0] == 'X' ? field.IndexOf('=') : 1;
            if (separator > 0) tags[field.Substring(0, separator)] = field.Substring(separator + (field[0] == 'X' ? 1 : 0));
        }
        int width, height;
        if (!int.TryParse(Tag(tags, "W", ""), NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
            !int.TryParse(Tag(tags, "H", ""), NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
            width != source.Width || height != source.Height || width <= 0 || height <= 0 ||
            width % 2 != 0 || height % 2 != 0 || (long)width * height > 40000000)
            throw new InvalidDataException("Unexpected Y4M dimensions.");
        string chroma = Tag(tags, "C", "");
        int depth = chroma == "420p10" || chroma == "420p10le" ? 10 :
            chroma == "420" || chroma == "420jpeg" || chroma == "420mpeg2" ? 8 : 0;
        if (depth == 0 || (hdr && depth != 10)) throw new InvalidDataException("Unsupported Y4M pixel format: " + chroma);
        string frame = ReadLine(stream);
        if (frame != "FRAME" && !frame.StartsWith("FRAME ", StringComparison.Ordinal))
            throw new InvalidDataException("Missing Y4M frame.");
        int pixels = checked(width * height), bytes = depth == 8 ? 1 : 2;
        byte[] data = new byte[checked((pixels + pixels / 2) * bytes)];
        for (int offset = 0; offset < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(data, offset, data.Length - offset);
            if (count == 0) throw new EndOfStreamException("Incomplete Y4M frame.");
            offset += count;
        }

        string range = Tag(tags, "XCOLORRANGE", source.ColorRange);
        bool full = range == "full" || range == "pc" || range == "jpeg";
        string matrix = hdr ? "bt2020nc" : Tag(tags, "XCOLORMATRIX", source.ColorSpace);
        string location = Tag(tags, "XCHROMALOC", "");
        bool center = location == "center" || (location.Length == 0 && chroma == "420jpeg");
        bool topLeft = location == "topleft" || location == "top-left";
        byte[] result = Convert(data, width, height, depth, full, matrix, center, topLeft, hdr, peak, token);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb) { Tag = full ? "Full" : "Limited" };
        try
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < height; y++) Marshal.Copy(result, y * width * 4, IntPtr.Add(bits.Scan0, y * bits.Stride), width * 4); }
            finally { bitmap.UnlockBits(bits); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static string Tag(Dictionary<string, string> tags, string key, string fallback)
    {
        string value;
        return (tags.TryGetValue(key, out value) && value.Length != 0 ? value : fallback ?? "").ToLowerInvariant();
    }

    private static string ReadLine(Stream stream)
    {
        var buffer = new List<byte>(64);
        for (int b = stream.ReadByte(); b >= 0; b = stream.ReadByte())
        {
            if (b == '\n') return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.Add((byte)b);
            if (buffer.Count > 4096) throw new InvalidDataException("Y4M header line too long.");
        }
        throw new EndOfStreamException("Incomplete Y4M header.");
    }

    private static byte[] Convert(byte[] data, int width, int height, int depth, bool full, string matrix,
        bool center, bool topLeft, bool hdr, int peak, CancellationToken token)
    {
        int pixels = width * height, cw = width / 2, ch = height / 2, chroma = cw * ch;
        int bytes = depth == 8 ? 1 : 2, scale = 1 << (depth - 8), neutral = 128 * scale;
        float black = full ? 0 : 16 * scale;
        float yf = 1f / (full ? (1 << depth) - 1 : 219 * scale);
        float cf = 1f / (full ? (1 << depth) - 1 : 224 * scale);
        bool bt2020 = matrix == "bt2020nc" || matrix == "bt2020ncl" || matrix == "bt2020-ncl";
        bool bt601 = matrix == "smpte170m" || matrix == "bt470bg" || matrix == "bt601" || matrix == "601";
        bool bt709 = matrix == "bt709" || matrix == "709" || (!bt601 && (width > 1024 || height > 576));
        float kr = bt2020 ? 0.2627f : matrix == "smpte240m" ? 0.212f : bt709 ? 0.2126f : 0.299f;
        float kb = bt2020 ? 0.0593f : matrix == "smpte240m" ? 0.087f : bt709 ? 0.0722f : 0.114f;
        float kg = 1 - kr - kb;
        float redCr = 2 * (1 - kr) * cf, blueCb = 2 * (1 - kb) * cf;
        float greenCb = 2 * kb * (1 - kb) / kg * cf, greenCr = 2 * kr * (1 - kr) / kg * cf;
        float normalizedPeak = Math.Max(peak, ReferenceWhite) / (float)ReferenceWhite;
        float shoulder = 1 / (1 - Knee) - 1 / (normalizedPeak - Knee);
        int plane = width * ch;
        var expanded = new float[plane * 2];
        var parallel = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, ch, parallel, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int x0, x1;
                double phase;
                Axis(x, center, cw, out x0, out x1, out phase);
                int u = pixels + y * cw, v = u + chroma, target = y * width + x;
                int u0 = Sample(data, u + x0, bytes), v0 = Sample(data, v + x0, bytes);
                expanded[target] = u0 + (Sample(data, u + x1, bytes) - u0) * (float)phase - neutral;
                expanded[plane + target] = v0 + (Sample(data, v + x1, bytes) - v0) * (float)phase - neutral;
            }
        });
        var output = new byte[pixels * 4];
        Parallel.For(0, height, parallel, y =>
        {
            int y0, y1;
            double fraction;
            Axis(y, !topLeft, ch, out y0, out y1, out fraction);
            int row = y * width, row0 = y0 * width, row1 = y1 * width;
            for (int x = 0; x < width; x++)
            {
                float cb = expanded[row0 + x], cr = expanded[plane + row0 + x];
                cb += (expanded[row1 + x] - cb) * (float)fraction;
                cr += (expanded[plane + row1 + x] - cr) * (float)fraction;
                float yp = (Sample(data, row + x, bytes) - black) * yf;
                float r = yp + redCr * cr, g = yp - greenCb * cb - greenCr * cr, b = yp + blueCb * cb;
                if (hdr)
                {
                    r = Lookup(Pq, r) / ReferenceWhite;
                    g = Lookup(Pq, g) / ReferenceWhite;
                    b = Lookup(Pq, b) / ReferenceWhite;
                    float maximum = Math.Max(r, Math.Max(g, b));
                    if (maximum > Knee)
                    {
                        float delta = maximum - Knee;
                        float mapped = maximum >= normalizedPeak ? 1 : Knee + delta / (1 + shoulder * delta);
                        float ratio = mapped / maximum;
                        r *= ratio; g *= ratio; b *= ratio;
                    }
                    float rr = 1.6604910021f * r - 0.5876411388f * g - 0.0728498633f * b;
                    float gg = -0.1245504745f * r + 1.1328998971f * g - 0.0083494226f * b;
                    float bb = -0.0181507634f * r - 0.1005788980f * g + 1.1187296614f * b;
                    r = Lookup(Gamma, (float)Math.Sqrt(Clamp(rr, 0, 1)));
                    g = Lookup(Gamma, (float)Math.Sqrt(Clamp(gg, 0, 1)));
                    b = Lookup(Gamma, (float)Math.Sqrt(Clamp(bb, 0, 1)));
                }
                else { r *= 255; g *= 255; b *= 255; }
                int target = (row + x) * 4;
                output[target] = (byte)Clamp(b + 0.5f, 0, 255);
                output[target + 1] = (byte)Clamp(g + 0.5f, 0, 255);
                output[target + 2] = (byte)Clamp(r + 0.5f, 0, 255);
                output[target + 3] = 255;
            }
        });
        return output;
    }

    private static float Clamp(float value, float min, float max) { return Math.Max(min, Math.Min(max, value)); }
    private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
    private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }

    private static float[] MakeTable(int size, Func<double, double> function)
    {
        var table = new float[size + 1];
        for (int i = 0; i <= size; i++) table[i] = (float)function((double)i / size);
        return table;
    }

    private static float Lookup(float[] table, float value)
    {
        int size = table.Length - 1;
        float position = Clamp(value, 0, 1) * size;
        int first = Math.Min((int)position, size - 1);
        return table[first] + (table[first + 1] - table[first]) * (position - first);
    }

    public static double PqToNits(double signal)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 32;
        const double c1 = 3424.0 / 4096, c2 = 2413.0 / 128, c3 = 2392.0 / 128;
        double p = Math.Pow(Clamp(signal, 0, 1), 1 / m2);
        return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    private static void Axis(int coordinate, bool centered, int length, out int first, out int second, out double fraction)
    {
        first = coordinate >> 1;
        if (centered)
        {
            bool even = (coordinate & 1) == 0;
            if (even) --first;
            fraction = even ? 0.75 : 0.25;
        }
        else fraction = (coordinate & 1) == 0 ? 0 : 0.5;
        second = Clamp(first + 1, 0, length - 1);
        first = Clamp(first, 0, length - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sample(byte[] data, int index, int bytes)
    {
        if (bytes == 1) return data[index];
        int offset = index * 2;
        return data[offset] | data[offset + 1] << 8;
    }

}
}
