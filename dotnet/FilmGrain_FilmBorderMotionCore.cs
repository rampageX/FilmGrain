using System;
using System.Collections.Generic;
using System.Globalization;

namespace FilmGrainStudioPreview
{
    // Presentation gate weave, separate from film texture/style and aperture geometry.
    // S4 formulas are retained exactly when all three controls are at 1.0.
    internal static class FilmBorderMotionCore
    {
        internal const string AmpKey = "FILM_BORDER_AMP";
        internal const string SpeedKey = "FILM_BORDER_SPEED";
        internal const string FrequencyKey = "FILM_BORDER_FREQUENCY";
        internal const string MoveX = "if(lt(mod(abs(sin(floor(t*13)*12.9898+23)*43758.5453),1),0.12),if(lt(mod(abs(sin(floor(t*13)*12.9898+23)*43758.5453),1),0.06),-2,2),0)+if(lt(mod(abs(sin(floor(t*29.97)*40.317+31)*17231.213),1),0.13),1,0)";
        internal const string MoveY = "if(lt(mod(abs(sin(floor(t*13)*78.233+97)*23577.731),1),0.19),if(lt(mod(abs(sin(floor(t*13)*78.233+97)*23577.731),1),0.07),-3,2),0)+if(lt(mod(abs(sin(floor(t*29.97)*91.789+17)*23577.731),1),0.17),1,0)";
        internal static double Parse(string input,double min,double max)
        {
            double result;
            if(!double.TryParse(input,NumberStyles.Float,CultureInfo.InvariantCulture,out result) ||
                double.IsInfinity(result) || double.IsNaN(result))return 1;
            return Math.Max(min,Math.Min(max,result));
        }
        private static string N(double value) {return value.ToString("0.###",CultureInfo.InvariantCulture);}
        internal static string Expression(bool x,double amp,double speed,double frequency)
        {
            string value=x?MoveX:MoveY;
            if(Math.Abs(amp-1)<1e-9 && Math.Abs(speed-1)<1e-9 && Math.Abs(frequency-1)<1e-9)
                return value;
            // The two independently seeded clocks produce a discrete gate registration jitter.
            // Frequency adjusts event probability; speed adjusts both slot clocks.
            if(Math.Abs(speed-1)>1e-9)
                value=value.Replace("floor(t*13)","floor(t*13*"+N(speed)+")")
                    .Replace("floor(t*29.97)","floor(t*29.97*"+N(speed)+")");
            if(Math.Abs(frequency-1)>1e-9)
            {
                string[,] rates={{"0.12","0.06"},{"0.19","0.07"},{"0.13","0.17"}};
                // Only numeric comparison thresholds are touched, never hash constants.
                foreach(string literal in new[]{"0.12","0.06","0.19","0.07","0.13","0.17"})
                    value=value.Replace(","+literal+")",","+N(Math.Min(1,double.Parse(literal,CultureInfo.InvariantCulture)*frequency))+")");
            }
            if(Math.Abs(amp-1)>1e-9)value="("+value+")*"+N(amp);
            return value;
        }
    }
}
