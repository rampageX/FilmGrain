using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FilmGrainStudioPreview
{
    internal enum GradeParameter
    {
        Exposure, Contrast, Highlights, Shadows, Whites, Blacks, Midtones,
        Temperature, Tint, Hue, Vibrance, Saturation
    }

    internal enum GradeSignal { Bt709, Pq, Hlg }

    internal sealed class ColorGrade
    {
        internal const string ConfigKey = "COLOR_GRADE";
        internal const string StateKey = "FG_COLOR_GRADE";
        internal const string Neutral = "";
        internal const int Count = 12;
        internal readonly int[] Values = new int[Count];
        internal StyleTransferStyle Style;
        internal bool IsActive { get { return HasAdjustments || (Style != null && Style.IsActive); } }
        internal bool HasAdjustments { get { return Array.Exists(Values, value => value != 0); } }

        internal static int Limit(int index)
        { return index == (int)GradeParameter.Exposure ? 60 : index == (int)GradeParameter.Hue ? 180 : 100; }

        internal string Serialize()
        {
            if (Style == null && !HasAdjustments) return Neutral;
            string sliders = string.Join(",", Array.ConvertAll(Values, value => value.ToString(CultureInfo.InvariantCulture)));
            return Style == null ? sliders : sliders + ",style2," + Style.Serialize();
        }

        internal static ColorGrade Parse(string text)
        {
            var grade = new ColorGrade();
            if (string.IsNullOrEmpty(text)) return grade;
            string[] parts = text.Split(',');
            if (parts.Length == Count + 9 && parts[Count] == "style2")
                grade.Style = StyleTransferStyle.Parse(parts, Count + 1, false);
            else if (parts.Length == Count + 6 && parts[Count] == "style1")
                grade.Style = StyleTransferStyle.Parse(parts, Count + 1, true);
            else if (parts.Length != Count)
                throw new InvalidDataException("Invalid color grade parameter count.");
            for (int i = 0; i < Count; i++)
            {
                int value;
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                    value < -Limit(i) || value > Limit(i))
                    throw new InvalidDataException("Invalid color grade parameter: " + (GradeParameter)i);
                grade.Values[i] = value;
            }
            return grade;
        }
    }

    internal struct GradeRgb
    {
        internal readonly double R, G, B;
        internal GradeRgb(double r, double g, double b) { R = r; G = g; B = b; }
    }

    internal sealed class ColorGradeTransform
    {
        private readonly GradeSignal signal;
        private readonly double exposure, contrast, highlights, shadows, whites, blacks, midtones;
        private readonly double temperature, tint, hueCos, hueSin, vibrance, saturation;
        private readonly bool neutral, color;
        private readonly FilmLutCube baseCube;
        private readonly StyleTransferStyle style;
        private readonly double strength;
        private readonly double[] sample = new double[3];

        internal ColorGradeTransform(ColorGrade grade, GradeSignal signal, FilmLutCube baseCube,
            int strength)
        {
            var v = grade.Values;
            this.signal = signal;
            this.baseCube = baseCube;
            this.strength = strength / 100.0;
            exposure = Math.Pow(2, v[0] * 0.05);
            contrast = v[1] * 0.01;
            highlights = v[2] * 0.01;
            shadows = v[3] * 0.01;
            whites = v[4] * 0.01;
            blacks = v[5] * 0.01;
            midtones = v[6] * 0.01;
            temperature = v[7] * 0.01;
            tint = v[8] * 0.01;
            double angle = v[9] * Math.PI / 180;
            hueCos = Math.Cos(angle);
            hueSin = Math.Sin(angle);
            vibrance = v[10] * 0.01;
            saturation = 1 + v[11] * 0.01;
            color = temperature != 0 || tint != 0 || v[9] != 0 || vibrance != 0 || saturation != 1;
            neutral = !grade.HasAdjustments;
            style = grade.Style;
        }

        internal static double Clamp(double value) { return FilmLutCore.Clamp(value); }
        private static double Square(double x) { return x * x; }

        internal GradeRgb Apply(GradeRgb rgb) { return Apply(rgb, sample); }

        internal GradeRgb Apply(GradeRgb rgb, double[] scratch)
        {
            if (style != null) rgb = style.Apply(rgb, signal);
            double r = rgb.R, g = rgb.G, b = rgb.B;
            if (!neutral)
            {
                if (exposure != 1)
                {
                    r = Encode(Decode(r, signal) * exposure, signal);
                    g = Encode(Decode(g, signal) * exposure, signal);
                    b = Encode(Decode(b, signal) * exposure, signal);
                }
                bool hdr = signal != GradeSignal.Bt709;
                double kr = hdr ? 0.2627 : 0.2126, kb = hdr ? 0.0593 : 0.0722;
                double kg = 1 - kr - kb;
                double y = Clamp(kr * r + kg * g + kb * b);
                double offset = contrast * (y - 0.5) * 0.75
                    + highlights * 0.19 * Square(4 * y * (1 - y)) * y * y
                    + shadows * 0.19 * Square(4 * y * (1 - y)) * Square(1 - y)
                    + whites * 0.24 * Math.Pow(y, 5)
                    + blacks * 0.24 * Math.Pow(1 - y, 5)
                    + midtones * 0.18 * Square(4 * y * (1 - y));
                double shift = Clamp(y + offset) - y;
                r = Clamp(r + shift); g = Clamp(g + shift); b = Clamp(b + shift);
                if (color)
                {
                    double l, a, c;
                    ToLab(new GradeRgb(r,g,b), signal, out l, out a, out c);
                    double weight = Math.Sqrt(Clamp(l));
                    a += (0.020 * temperature + 0.050 * tint) * weight;
                    c += (0.080 * temperature - 0.015 * tint) * weight;
                    double scale = saturation * (1 + vibrance * (1 - Clamp(Math.Sqrt(a*a+c*c) * 4)));
                    var result = FromLab(l, (a*hueCos-c*hueSin)*scale, (a*hueSin+c*hueCos)*scale, signal);
                    r = result.R; g = result.G; b = result.B;
                }
            }
            if (baseCube != null && strength > 0)
            {
                baseCube.Sample(r, g, b, scratch);
                r += strength * (scratch[0] - r);
                g += strength * (scratch[1] - g);
                b += strength * (scratch[2] - b);
            }
            return new GradeRgb(Clamp(r), Clamp(g), Clamp(b));
        }

        private static double Decode(double x, GradeSignal signal)
        {
            if (signal == GradeSignal.Pq)
            {
                double n = Math.Pow(x, 32.0 / 2523);
                return Math.Pow(Math.Max(0, n - 3424.0/4096) / (2413.0/128 - 2392.0/128*n), 16384.0/2610);
            }
            if (signal == GradeSignal.Hlg)
                return x <= 0.5 ? x*x/3 : (Math.Exp((x-0.55991073)/0.17883277)+0.28466892)/12;
            return x < 0.081 ? x/4.5 : Math.Pow((x+0.099)/1.099, 1/0.45);
        }

        private static double Encode(double x, GradeSignal signal)
        {
            x = Clamp(x);
            if (signal == GradeSignal.Pq)
            {
                if (x == 0) return 0;
                double n = Math.Pow(x, 2610.0/16384);
                return Math.Pow((3424.0/4096 + 2413.0/128*n)/(1+2392.0/128*n), 2523.0/32);
            }
            if (signal == GradeSignal.Hlg)
                return x <= 1.0/12 ? Math.Sqrt(3*x) : 0.17883277*Math.Log(12*x-0.28466892)+0.55991073;
            return x < 0.018 ? 4.5*x : 1.099*Math.Pow(x,0.45)-0.099;
        }

        private static double Cbrt(double x) { return x < 0 ? -Math.Pow(-x, 1.0/3) : Math.Pow(x, 1.0/3); }
        private static double Cube(double x) { return x*x*x; }

        internal static void ToLab(GradeRgb rgb, GradeSignal signal, out double l, out double a, out double b)
        {
            double r = Decode(rgb.R,signal), g = Decode(rgb.G,signal), bl = Decode(rgb.B,signal);
            if (signal != GradeSignal.Bt709)
            {
                double scale = signal == GradeSignal.Pq ? 100 : 1;
                double rr = (1.660491*r-0.587641*g-0.072850*bl)*scale;
                double gg = (-0.124550*r+1.132900*g-0.008349*bl)*scale;
                bl = (-0.018151*r-0.100579*g+1.118730*bl)*scale;
                r = rr; g = gg;
            }
            double x = Cbrt(0.4122214708*r+0.5363325363*g+0.0514459929*bl);
            double y = Cbrt(0.2119034982*r+0.6806995451*g+0.1073969566*bl);
            double z = Cbrt(0.0883024619*r+0.2817188376*g+0.6299787005*bl);
            l = 0.2104542553*x+0.7936177850*y-0.0040720468*z;
            a = 1.9779984951*x-2.4285922050*y+0.4505937099*z;
            b = 0.0259040371*x+0.7827717662*y-0.8086757660*z;
        }

        private static GradeRgb FromLab(double l, double a, double b, GradeSignal signal)
        {
            GradeRgb rgb = FromLabLinear(l, a, b, signal);
            return new GradeRgb(Encode(rgb.R,signal), Encode(rgb.G,signal), Encode(rgb.B,signal));
        }

        internal static GradeRgb FromLabCompressed(double l, double a, double b, GradeSignal signal)
        {
            GradeRgb rgb = FromLabLinear(l, a, b, signal);
            if (!InGamut(rgb))
            {
                double low = 0, high = 1;
                for (int i = 0; i < 10; i++)
                {
                    double middle = (low + high) * 0.5;
                    if (InGamut(FromLabLinear(l, a * middle, b * middle, signal))) low = middle;
                    else high = middle;
                }
                rgb = FromLabLinear(l, a * low, b * low, signal);
            }
            return new GradeRgb(Encode(rgb.R,signal), Encode(rgb.G,signal), Encode(rgb.B,signal));
        }

        private static bool InGamut(GradeRgb rgb)
        {
            const double tolerance = 0.000001;
            return rgb.R >= -tolerance && rgb.R <= 1 + tolerance &&
                rgb.G >= -tolerance && rgb.G <= 1 + tolerance &&
                rgb.B >= -tolerance && rgb.B <= 1 + tolerance;
        }

        private static GradeRgb FromLabLinear(double l, double a, double b, GradeSignal signal)
        {
            double x = Cube(l+0.3963377774*a+0.2158037573*b);
            double y = Cube(l-0.1055613458*a-0.0638541728*b);
            double z = Cube(l-0.0894841775*a-1.2914855480*b);
            double r = 4.0767416621*x-3.3077115913*y+0.2309699292*z;
            double g = -1.2684380046*x+2.6097574011*y-0.3413193965*z;
            double bl = -0.0041960863*x-0.7034186147*y+1.7076147010*z;
            if (signal != GradeSignal.Bt709)
            {
                double scale = signal == GradeSignal.Pq ? 0.01 : 1;
                double rr = (0.627404*r+0.329283*g+0.043313*bl)*scale;
                double gg = (0.069097*r+0.919540*g+0.011362*bl)*scale;
                bl = (0.016391*r+0.088013*g+0.895595*bl)*scale;
                r = rr; g = gg;
            }
            return new GradeRgb(r,g,bl);
        }

        internal static double SrgbTo709(double x)
        {
            return Encode(x <= 0.04045 ? x/12.92 : Math.Pow((x+0.055)/1.055,2.4), GradeSignal.Bt709);
        }
    }

    internal sealed class StyleTransferStyle
    {
        private const double Scale = 10000;
        private static readonly int[] Limits = { 1200, 2000, 900, 900, 2500 };
        private readonly int[] values;
        internal readonly int Intensity, Color, Finish;
        internal bool IsActive { get { return Intensity > 0 && (Color > 0 || Finish > 0); } }

        private StyleTransferStyle(int[] values, int intensity, int color, int finish)
        {
            this.values = values;
            Intensity = intensity;
            Color = color;
            Finish = finish;
        }

        internal StyleTransferStyle WithControls(int intensity, int color, int finish)
        { return new StyleTransferStyle(values, intensity, color, finish); }

        internal string Serialize()
        {
            var recipe = new int[8];
            Array.Copy(values, recipe, values.Length);
            recipe[5] = Intensity;
            recipe[6] = Color;
            recipe[7] = Finish;
            return string.Join(",", Array.ConvertAll(recipe, x => x.ToString(CultureInfo.InvariantCulture)));
        }

        internal static StyleTransferStyle Parse(string[] parts, int offset, bool legacy)
        {
            var amounts = new int[Limits.Length];
            for (int i = 0; i < amounts.Length; i++)
            {
                if (!int.TryParse(parts[offset + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out amounts[i]) ||
                    amounts[i] < -Limits[i] || amounts[i] > Limits[i])
                    throw new InvalidDataException("Invalid style transfer recipe.");
            }
            if (legacy) return new StyleTransferStyle(amounts, 100, 100, 100);
            var controls = new int[3];
            for (int i = 0; i < controls.Length; i++)
            {
                if (!int.TryParse(parts[offset + amounts.Length + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out controls[i]) ||
                    controls[i] < 0 || controls[i] > 200)
                    throw new InvalidDataException("Invalid style transfer strength.");
            }
            return new StyleTransferStyle(amounts, controls[0], controls[1], controls[2]);
        }

        internal static ColorGrade Match(GradeRgb[] source, GradeRgb[] reference, GradeSignal signal,
            int strength, CancellationToken cancel)
        {
            if (source.Length < 16 || reference.Length < 16)
                throw new InvalidDataException("Too few image samples.");
            if (strength < 0 || strength > 100) throw new ArgumentOutOfRangeException("strength");
            var from = new Palette(source, signal, cancel);
            var to = new Palette(reference, GradeSignal.Bt709, cancel);

            // Compare relative brightness for HDR. The source highlight level anchors
            // the transfer, so an SDR reference cannot set an HDR peak level.
            double referenceScale = signal == GradeSignal.Bt709 ? 1 :
                Math.Max(0.01, from.High) / Math.Max(0.01, to.High);
            double lift = to.Middle * referenceScale - from.Middle;
            double spread = Math.Max(0.07, from.High - from.Low);
            double contrast = ((to.High - to.Low) * referenceScale / spread - 1) * 0.4;
            double saturation = (to.Chroma / Math.Max(0.025, from.Chroma) - 1) * 0.45;
            double[] changes = { lift, contrast, (to.A - from.A) * 0.65, (to.B - from.B) * 0.65, saturation };
            var amounts = new int[changes.Length];
            for (int i = 0; i < amounts.Length; i++)
            {
                double bounded = Math.Max(-Limits[i], Math.Min(Limits[i], changes[i] * Scale));
                amounts[i] = (int)Math.Round(bounded);
            }
            return new ColorGrade {
                Style = new StyleTransferStyle(amounts, strength, 100, 100)
            };
        }

        internal GradeRgb Apply(GradeRgb input, GradeSignal signal)
        {
            if (!IsActive) return input;
            double l, a, b;
            ColorGradeTransform.ToLab(input, signal, out l, out a, out b);
            double maximum = LightnessMaximum(signal);
            double t = ColorGradeTransform.Clamp(l / maximum);
            double envelope = 4 * t * (1 - t);
            double overall = Intensity / 100.0;
            double tonal = Math.Min(2, overall * Finish / 100);
            double color = overall * Color / 100;
            double adjusted = t + tonal * envelope * (values[0] + values[1] * (t - 0.5)) / Scale;
            double saturation = 1 + color * values[4] / Scale;

            // Transfer a restrained tint even to neutral input, while tapering
            // it toward signal black and white.
            double tintWeight = l * envelope * color;
            return ColorGradeTransform.FromLabCompressed(adjusted * maximum,
                a * saturation + tintWeight * values[2] / Scale,
                b * saturation + tintWeight * values[3] / Scale, signal);
        }

        private static double LightnessMaximum(GradeSignal signal)
        { return signal == GradeSignal.Pq ? 4.641588833612779 : 1; }

        private sealed class Palette
        {
            internal readonly double Low, Middle, High, A, B, Chroma;

            internal Palette(GradeRgb[] pixels, GradeSignal signal, CancellationToken cancel)
            {
                var lightness = new double[pixels.Length];
                var aAxis = new double[pixels.Length];
                var bAxis = new double[pixels.Length];
                var chroma = new double[pixels.Length];
                double maximum = LightnessMaximum(signal);
                for (int i = 0; i < pixels.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    double l, a, b;
                    ColorGradeTransform.ToLab(pixels[i], signal, out l, out a, out b);
                    lightness[i] = l / maximum;
                    double denominator = Math.Max(l, 0.04);
                    aAxis[i] = a / denominator;
                    bAxis[i] = b / denominator;
                    chroma[i] = Math.Sqrt(aAxis[i] * aAxis[i] + bAxis[i] * bAxis[i]);
                }
                Array.Sort(lightness);
                Array.Sort(aAxis);
                Array.Sort(bAxis);
                Array.Sort(chroma);
                Low = Quantile(lightness, 0.10);
                Middle = Quantile(lightness, 0.50);
                High = Quantile(lightness, 0.90);
                A = Quantile(aAxis, 0.50);
                B = Quantile(bAxis, 0.50);
                Chroma = Quantile(chroma, 0.70);
            }

            private static double Quantile(double[] sorted, double fraction)
            {
                double index = fraction * (sorted.Length - 1);
                int lower = (int)index;
                return sorted[lower] + (index - lower) * (sorted[lower + 1] - sorted[lower]);
            }
        }
    }

    internal static class ColorGradeCore
    {
        internal const string OriginalLutKey = "FG_COLOR_GRADE_BASE_LUT";

        internal static string OutputSuffix(string recipe)
        {
            using(var sha=System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(recipe));
                return "_CC_"+BitConverter.ToString(hash,0,4).Replace("-","").ToLowerInvariant();
            }
        }

        internal static ColorGradeTransform Compose(ColorGrade grade, GradeSignal signal, string path, int strength)
        {
            var cube = string.IsNullOrEmpty(path) || strength <= 0 ? null : FilmLutCube.Read(path,true);
            return new ColorGradeTransform(grade,signal,cube,strength);
        }

        internal static GradeSignal Signal(MediaProbeInfo info)
        { return info == null || !info.IsHdr ? GradeSignal.Bt709 : info.ColorTransfer == "smpte2084" ? GradeSignal.Pq : GradeSignal.Hlg; }

        internal static string SignalName(GradeSignal signal)
        {
            return signal == GradeSignal.Pq ? "BT.2020 PQ HDR10" :
                signal == GradeSignal.Hlg ? "BT.2020 HLG" : "SDR BT.709";
        }

        internal static void WriteCube(string path, int size, GradeSignal signal, ColorGradeTransform transform, bool interactive = false)
        {
            if (size != 33 && size != 65) throw new ArgumentOutOfRangeException("size");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    string name = SignalName(signal);
                    writer.WriteLine("# FGS color grade. Input and output: " + name + " full-range RGB code values [0,1].");
                    writer.WriteLine("TITLE \"FGS Color Grade - " + name + "\"");
                    writer.WriteLine("LUT_3D_SIZE " + size.ToString(CultureInfo.InvariantCulture));
                    double step = 1.0 / (size-1);
                    if (interactive)
                    {
                        var slices = new string[size];
                        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
                        Parallel.For(0,size,options,b =>
                        {
                            var lines = new StringBuilder(size * size * 30);
                            var scratch = new double[3];
                            for (int g = 0; g < size; g++)
                                for (int r = 0; r < size; r++)
                                {
                                    var result = transform.Apply(new GradeRgb(r*step,g*step,b*step),scratch);
                                    lines.AppendFormat(CultureInfo.InvariantCulture,"{0:F7} {1:F7} {2:F7}",result.R,result.G,result.B);
                                    lines.AppendLine();
                                }
                            slices[b] = lines.ToString();
                        });
                        foreach (string slice in slices) writer.Write(slice);
                    }
                    else
                    {
                        for (int b = 0; b < size; b++)
                            for (int g = 0; g < size; g++)
                                for (int r = 0; r < size; r++)
                                {
                                    GradeRgb result = transform.Apply(new GradeRgb(r*step,g*step,b*step));
                                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F7} {1:F7} {2:F7}", result.R,result.G,result.B));
                                }
                    }
                }
                if (File.Exists(path)) File.Replace(temporary,path,null);
                else File.Move(temporary,path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static ColorGrade Match(GradeRgb[] source, GradeRgb[] reference, GradeSignal signal,
            FilmLutCube baseCube, int strength, CancellationToken cancel, int matchStrength=100)
        {
            if (source.Length < 16 || reference.Length < 16) throw new InvalidDataException("Too few image samples.");
            var grade = new ColorGrade();
            if(matchStrength<0||matchStrength>100)throw new ArgumentOutOfRangeException("matchStrength");
            cancel.ThrowIfCancellationRequested();
            if(matchStrength==0)return grade;
            var target = Statistics(reference, GradeSignal.Bt709, null);
            if(matchStrength<100)
            {
                var original=Statistics(source,signal,new ColorGradeTransform(grade,signal,baseCube,strength));
                for(int i=0;i<target.Length;i++)
                    target[i]=original[i]+(target[i]-original[i])*matchStrength/100.0;
            }
            double best = Score(source, target, signal, baseCube, strength, grade);
            // Bounded coordinate search fits sorted distributions, with a small
            // regularizer to favor modest changes when multiple controls overlap.
            foreach (int step in new[] {32,16,8,4,2,1})
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < ColorGrade.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int start = grade.Values[i], chosen = start, limit = ColorGrade.Limit(i);
                        foreach (int direction in new[] {-1,1})
                        {
                            grade.Values[i] = Math.Max(-limit,Math.Min(limit,start+step*direction));
                            double score = Score(source,target,signal,baseCube,strength,grade);
                            if (score < best) { best=score; chosen=grade.Values[i]; }
                        }
                        grade.Values[i]=chosen;
                    }
            return grade;
        }

        private static double Score(GradeRgb[] source, double[] target, GradeSignal signal, FilmLutCube baseCube, int strength, ColorGrade grade)
        {
            var stats = Statistics(source,signal,new ColorGradeTransform(grade,signal,baseCube,strength));
            double loss=0;
            for (int i=0;i<stats.Length;i++)
            {
                double delta=stats[i]-target[i];
                loss += (i<9?2.0:4.0)*delta*delta;
            }
            for (int i=0;i<ColorGrade.Count;i++)
            {
                double relative=grade.Values[i]/(double)ColorGrade.Limit(i);
                loss += 0.0005*relative*relative;
            }
            return loss;
        }

        private static double[] Statistics(GradeRgb[] pixels, GradeSignal signal, ColorGradeTransform transform)
        {
            var axes = new double[6][];
            for(int i=0;i<axes.Length;i++) axes[i]=new double[pixels.Length];
            for(int i=0;i<pixels.Length;i++)
            {
                double l,a,b;
                ColorGradeTransform.ToLab(transform==null?pixels[i]:transform.Apply(pixels[i]),signal,out l,out a,out b);
                axes[0][i]=l; axes[1][i]=a; axes[2][i]=b;
                axes[3][i]=(a+b)*0.70710678; axes[4][i]=(a-b)*0.70710678;
                axes[5][i]=Math.Sqrt(a*a+b*b);
            }
            var statistics=new double[axes.Length*9];
            for(int axis=0;axis<axes.Length;axis++)
            {
                double[] sorted=axes[axis];
                Array.Sort(sorted);
                for(int q=0;q<9;q++)
                {
                    double position=(0.05+q*0.1125)*(sorted.Length-1);
                    int low=(int)position;
                    statistics[axis*9+q]=sorted[low]+(position-low)*(sorted[low+1]-sorted[low]);
                }
            }
            return statistics;
        }
    }
}
