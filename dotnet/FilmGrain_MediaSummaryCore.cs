using System;
using System.Globalization;

namespace FilmGrainStudioPreview
{
    internal sealed class MediaSummaryCore
    {
        private readonly LanguagePack lang;

        internal MediaSummaryCore(LanguagePack language)
        {
            lang = language;
        }

        private string LF(string key, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, lang.T(key), args); }
            catch { return lang.T(key); }
        }

        internal string FormatMediaSummary(MediaProbeInfo info)
        {
            string videoLine;
            if (info.HasVideo)
            {
                string resolution = info.Width > 0 && info.Height > 0 ? info.Width.ToString(CultureInfo.InvariantCulture) + "×" + info.Height.ToString(CultureInfo.InvariantCulture) : "—";
                string fps = FormatMediaFps(info.AvgFrameRate);
                string field = (info.FieldOrder ?? "").ToLowerInvariant();
                string scanText;
                if (info.IsInterlaced) scanText = LF("media.scan_interlaced", field);
                else if (field == "progressive") scanText = lang.T("media.scan_progressive");
                else if (!string.IsNullOrEmpty(field)) scanText = LF("media.scan_flag", field);
                else scanText = lang.T("media.scan_unknown");
                videoLine = lang.T("media.video_prefix") + "  " + FormatCodecName(info.VideoCodec, info.VideoProfile) + " · " + resolution + " · " + fps + " fps · " + scanText + " · " + FormatMediaBitrate(info.VideoBitRate);

                if (info.IsHdr)
                {
                    string transfer = (info.ColorTransfer ?? "").ToLowerInvariant();
                    string hdrName = transfer == "smpte2084" ? "HDR10 / PQ" : "HLG";
                    string bitDepth = "10-bit";
                    int bits;
                    if (int.TryParse(info.BitsPerRawSample, NumberStyles.Integer, CultureInfo.InvariantCulture, out bits) && bits > 0)
                        bitDepth = bits.ToString(CultureInfo.InvariantCulture) + "-bit";
                    else if (!string.IsNullOrEmpty(info.PixelFormat))
                    {
                        if (info.PixelFormat.IndexOf("12", StringComparison.OrdinalIgnoreCase) >= 0) bitDepth = "12-bit";
                        else if (info.PixelFormat.IndexOf("10", StringComparison.OrdinalIgnoreCase) >= 0) bitDepth = "10-bit";
                    }
                    string prim = string.IsNullOrEmpty(info.ColorPrimaries) ? "primaries ?" : info.ColorPrimaries;
                    string matrix = string.IsNullOrEmpty(info.ColorSpace) ? "matrix ?" : info.ColorSpace;
                    videoLine += Environment.NewLine + "HDR   " + hdrName + " · " + bitDepth + " · " + prim + " · " + matrix;
                }
            }
            else
            {
                videoLine = lang.T("media.video_missing");
            }

            string audioLine;
            if (info.HasAudio)
            {
                string channels = !string.IsNullOrEmpty(info.ChannelLayout) ? info.ChannelLayout : (info.Channels > 0 ? LF("media.channel_count", info.Channels) : lang.T("media.channel_unknown"));
                string sample = "—";
                double sampleRate;
                if (double.TryParse(info.SampleRate, NumberStyles.Float, CultureInfo.InvariantCulture, out sampleRate) && sampleRate > 0)
                    sample = (sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";
                audioLine = lang.T("media.audio_prefix") + "  " + FormatCodecName(info.AudioCodec, info.AudioProfile) + " · " + channels + " · " + sample + " · " + FormatMediaBitrate(info.AudioBitRate);
            }
            else
            {
                audioLine = lang.T("media.audio_missing");
            }

            string formatLine = LF("media.duration_line", FormatMediaDuration(info.Duration), FormatMediaBitrate(info.TotalBitRate));
            return videoLine + Environment.NewLine + audioLine + Environment.NewLine + formatLine;
        }

        private static string FormatMediaBitrate(string value)
        {
            long rate;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate) || rate <= 0) return "—";
            if (rate >= 1000000) return (rate / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mb/s";
            return (rate / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kb/s";
        }

        private static string FormatMediaDuration(string value)
        {
            double seconds;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds < 0) return "—";
            int hours = (int)Math.Floor(seconds / 3600.0);
            int minutes = (int)Math.Floor((seconds % 3600.0) / 60.0);
            double remaining = seconds % 60.0;
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + remaining.ToString("00.000", CultureInfo.InvariantCulture);
        }

        internal static string FormatMediaFps(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            string[] parts = value.Split('/');
            double numerator;
            double denominator;
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) && denominator != 0)
                return (numerator / denominator).ToString("0.###", CultureInfo.InvariantCulture);
            return value;
        }

        private static string FormatCodecName(string codecName, string profile)
        {
            string codec = string.IsNullOrEmpty(codecName) ? "—" : codecName.ToUpperInvariant();
            if (!string.IsNullOrEmpty(profile) && !string.Equals(profile, "unknown", StringComparison.OrdinalIgnoreCase) && !string.Equals(profile, "N/A", StringComparison.OrdinalIgnoreCase))
                return codec + " · " + profile;
            return codec;
        }

        internal static string DoubleFpsDisplay(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split('/');
            double numerator;
            double denominator;
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) && denominator != 0)
                return ((numerator * 2.0) / denominator).ToString("0.###", CultureInfo.InvariantCulture);
            double fps;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) ? (fps * 2.0).ToString("0.###", CultureInfo.InvariantCulture) : "";
        }


    }
}
