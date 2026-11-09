using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    // A tiny offline LUT editor. The film models were generated with the
    // MIT-licensed Spectral Film LUT v0.18.13, not approximated at runtime.
    // All inputs here are SDR BT.709 code values; HDR is deliberately excluded.
    internal sealed class FilmLutProfile
    {
        internal readonly string Id;
        internal readonly string Chinese;
        internal readonly string English;
        internal FilmLutProfile(string id, string chinese, string english)
        { Id=id; Chinese=chinese; English=english; }
    }

    internal sealed class FilmLutSettings
    {
        public string ProfileId { get; set; }
        public int ExposureTenths { get; set; }
        public int Warmth { get; set; }
        public int Saturation { get; set; }
        public int Strength { get; set; }

        internal static FilmLutSettings Default(string profile)
        { return new FilmLutSettings { ProfileId=profile, ExposureTenths=0, Warmth=0, Saturation=100, Strength=100 }; }

        internal FilmLutSettings Copy()
        { return new FilmLutSettings {ProfileId=ProfileId,ExposureTenths=ExposureTenths,Warmth=Warmth,Saturation=Saturation,Strength=Strength}; }

        internal void Validate()
        {
            if(ExposureTenths < -10 || ExposureTenths > 10 || Warmth < -40 || Warmth > 40 ||
                Saturation < 70 || Saturation > 130 || Strength < 0 || Strength > 100)
                throw new InvalidDataException("Film LUT parameter is outside its supported range.");
            if(FilmLutCore.Profile(ProfileId) == null)
                throw new InvalidDataException("Unknown film LUT profile.");
        }
    }

    internal sealed class FilmLutCube
    {
        internal readonly int Size;
        private readonly float[] values;

        private FilmLutCube(int size, float[] entries)
        { Size=size; values=entries; }

        internal static FilmLutCube Read(string path)
        {
            int size=0, count=0;
            float[] values=null;
            foreach(string line in File.ReadLines(path)) {
                string trimmed=line.Split('#')[0].Trim();
                if(trimmed.Length==0)continue;
                string[] parts=trimmed.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                if(parts[0].Equals("LUT_3D_SIZE",StringComparison.OrdinalIgnoreCase)) {
                    if(size!=0 || parts.Length!=2 || !int.TryParse(parts[1],out size) || size<2 || size>65)
                        throw new InvalidDataException("Invalid film LUT size.");
                    values=new float[size*size*size*3];
                    continue;
                }
                if(parts.Length!=3 || size==0)continue;
                float r,g,b;
                if(!float.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out r) ||
                    !float.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out g) ||
                    !float.TryParse(parts[2],NumberStyles.Float,CultureInfo.InvariantCulture,out b))continue;
                if(count>=size*size*size || !Finite(r) || !Finite(g) || !Finite(b))
                    throw new InvalidDataException("Invalid film LUT sample.");
                values[3*count]=r;values[3*count+1]=g;values[3*count+2]=b;count++;
            }
            if(size==0 || count!=size*size*size)throw new InvalidDataException("Incomplete film LUT.");
            return new FilmLutCube(size,values);
        }

        private static bool Finite(float v) {return !float.IsNaN(v) && !float.IsInfinity(v);}
        private int Index(int r,int g,int b) {return 3*((b*Size+g)*Size+r);}
        private double Component(int r,int g,int b,int channel)
        {return values[Index(r,g,b)+channel];}

        // Matches FFmpeg lut3d's tetrahedral interpolation and CUBE's R-fastest order.
        internal void Sample(double r,double g,double b,double[] output)
        {
            double x=FilmLutCore.Clamp(r)*(Size-1), y=FilmLutCore.Clamp(g)*(Size-1), z=FilmLutCore.Clamp(b)*(Size-1);
            int r0=Math.Min(Size-2,(int)x), g0=Math.Min(Size-2,(int)y), b0=Math.Min(Size-2,(int)z);
            int r1=r0+1,g1=g0+1,b1=b0+1;
            double dr=x-r0,dg=y-g0,db=z-b0;
            for(int c=0;c<3;c++) {
                double o=Component(r0,g0,b0,c);
                if(dr>=dg) {
                    if(dg>=db) o+=dr*(Component(r1,g0,b0,c)-Component(r0,g0,b0,c))+
                        dg*(Component(r1,g1,b0,c)-Component(r1,g0,b0,c))+
                        db*(Component(r1,g1,b1,c)-Component(r1,g1,b0,c));
                    else if(dr>=db) o+=dr*(Component(r1,g0,b0,c)-Component(r0,g0,b0,c))+
                        db*(Component(r1,g0,b1,c)-Component(r1,g0,b0,c))+
                        dg*(Component(r1,g1,b1,c)-Component(r1,g0,b1,c));
                    else o+=db*(Component(r0,g0,b1,c)-Component(r0,g0,b0,c))+
                        dr*(Component(r1,g0,b1,c)-Component(r0,g0,b1,c))+
                        dg*(Component(r1,g1,b1,c)-Component(r1,g0,b1,c));
                } else {
                    if(db>=dg) o+=db*(Component(r0,g0,b1,c)-Component(r0,g0,b0,c))+
                        dg*(Component(r0,g1,b1,c)-Component(r0,g0,b1,c))+
                        dr*(Component(r1,g1,b1,c)-Component(r0,g1,b1,c));
                    else if(db>=dr) o+=dg*(Component(r0,g1,b0,c)-Component(r0,g0,b0,c))+
                        db*(Component(r0,g1,b1,c)-Component(r0,g1,b0,c))+
                        dr*(Component(r1,g1,b1,c)-Component(r0,g1,b1,c));
                    else o+=dg*(Component(r0,g1,b0,c)-Component(r0,g0,b0,c))+
                        dr*(Component(r1,g1,b0,c)-Component(r0,g1,b0,c))+
                        db*(Component(r1,g1,b1,c)-Component(r1,g1,b0,c));
                }
                output[c]=o;
            }
        }
    }

    internal static class FilmLutCore
    {
        private static readonly string SessionRoot=Path.Combine(Path.GetTempPath(),"FilmGrain_Studio",
            "FilmLutSession_"+Guid.NewGuid().ToString("N"));

        static FilmLutCore()
        {
            // Unsaved 'Apply' LUTs must survive the editor closing and any
            // pending encoder job. They are removed only at FGS process exit.
            AppDomain.CurrentDomain.ProcessExit+=delegate{
                try{if(Directory.Exists(SessionRoot))Directory.Delete(SessionRoot,true);}catch{}
            };
        }

        internal static string WriteSessionCube(string appRoot,FilmLutSettings settings)
        {
            FilmLutSettings full=settings.Copy();full.Strength=100;
            full.Validate();
            Directory.CreateDirectory(SessionRoot);
            string path=Path.Combine(SessionRoot,"FGS_FilmLUT_"+Guid.NewGuid().ToString("N")+".cube");
            WriteCube(path,FilmLutCube.Read(ProfilePath(appRoot,full.ProfileId)),full);
            return path;
        }
        // Byte-reference preview cache: transfer functions and exposure are
        // calculated 3x256 times per drag, rather than per video-frame pixel.
        internal sealed class PreviewTransform
        {
            internal readonly double[][] Channels=new double[3][];
            internal readonly double Saturation,Strength;
            internal PreviewTransform(FilmLutSettings settings)
            {
                double exposure=Math.Pow(2.0,settings.ExposureTenths/10.0);
                double warmth=settings.Warmth/100.0;
                double[] gains={1+0.6*warmth,1.0,1-0.6*warmth};
                for(int c=0;c<3;c++) {
                    Channels[c]=new double[256];
                    for(int i=0;i<256;i++)
                        Channels[c][i]=Clamp(Encode709(Clamp(Decode709(i/255.0)*exposure*gains[c])));
                }
                Saturation=settings.Saturation/100.0;
                Strength=settings.Strength/100.0;
            }
        }
        internal static readonly FilmLutProfile[] Profiles={
            new FilmLutProfile("Vision3_50D_2383","Vision3 50D · 2383","Vision3 50D · 2383"),
            new FilmLutProfile("Vision3_250D_2383","Vision3 250D · 2383","Vision3 250D · 2383"),
            new FilmLutProfile("Vision3_500T_2383","Vision3 500T · 2383","Vision3 500T · 2383"),
            new FilmLutProfile("Vision3_500T_2393","Vision3 500T · 2393","Vision3 500T · 2393"),
            new FilmLutProfile("Eterna500_3513DI","Eterna 500 · 3513DI","Eterna 500 · 3513DI")
        };

        internal static FilmLutProfile Profile(string id)
        {
            foreach(FilmLutProfile profile in Profiles)if(profile.Id==id)return profile;
            return null;
        }

        internal static string ProfilePath(string appRoot,string id)
        {
            if(Profile(id)==null)throw new InvalidDataException("Unknown film stock.");
            return Path.Combine(appRoot,"_LUT_Tools","FilmLut","Profiles",id+".cube");
        }

        internal static double Clamp(double n) {return n<0?0:n>1?1:n;}

        private static double Decode709(double code)
        {return code<0.081?code/4.5:Math.Pow((code+0.099)/1.099,1.0/0.45);}
        private static double Encode709(double linear)
        {return linear<0.018?4.5*linear:1.099*Math.Pow(linear,0.45)-0.099;}

        internal static void ProcessByte(FilmLutCube baseCube,PreviewTransform transform,
            byte red,byte green,byte blue,double[] output)
        {
            baseCube.Sample(transform.Channels[0][red],transform.Channels[1][green],
                transform.Channels[2][blue],output);
            double y=0.2126*output[0]+0.7152*output[1]+0.0722*output[2];
            double strength=transform.Strength,saturation=transform.Saturation;
            output[0]=Clamp((red/255.0)*(1-strength)+Clamp(y+(output[0]-y)*saturation)*strength);
            output[1]=Clamp((green/255.0)*(1-strength)+Clamp(y+(output[1]-y)*saturation)*strength);
            output[2]=Clamp((blue/255.0)*(1-strength)+Clamp(y+(output[2]-y)*saturation)*strength);
        }

        // Creative temperature is deliberately expressed as cold/warm, not physical
        // Kelvin. Exposure is adjusted in scene-linear BT.709 before the film curve.
        internal static void Process(FilmLutCube baseCube,FilmLutSettings settings,
            double red,double green,double blue,double[] output)
        {
            double r=Clamp(red),g=Clamp(green),b=Clamp(blue);
            double exposure=Math.Pow(2.0,settings.ExposureTenths/10.0);
            double warmth=settings.Warmth/100.0;
            double ar=Clamp(Encode709(Clamp(Decode709(r)*exposure*(1+0.6*warmth))));
            double ag=Clamp(Encode709(Clamp(Decode709(g)*exposure)));
            double ab=Clamp(Encode709(Clamp(Decode709(b)*exposure*(1-0.6*warmth))));
            baseCube.Sample(ar,ag,ab,output);
            double y=0.2126*output[0]+0.7152*output[1]+0.0722*output[2];
            double saturation=settings.Saturation/100.0,strength=settings.Strength/100.0;
            output[0]=Clamp(r*(1-strength)+Clamp(y+(output[0]-y)*saturation)*strength);
            output[1]=Clamp(g*(1-strength)+Clamp(y+(output[1]-y)*saturation)*strength);
            output[2]=Clamp(b*(1-strength)+Clamp(y+(output[2]-y)*saturation)*strength);
        }

        internal static void WriteCube(string path,FilmLutCube baseCube,FilmLutSettings settings)
        {
            settings.Validate();
            const int size=33;
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(false))) {
                writer.WriteLine("# FGS Film LUT · SDR BT.709 input / Rec.709 Gamma 2.4 output");
                writer.WriteLine("# Source: Spectral Film LUT v0.18.13 (MIT); edited by Film Grain Studio");
                writer.WriteLine("TITLE \"FGS Film LUT "+settings.ProfileId+"\"");
                writer.WriteLine("LUT_3D_SIZE "+size.ToString(CultureInfo.InvariantCulture));
                var output=new double[3];
                for(int b=0;b<size;b++)for(int g=0;g<size;g++)for(int r=0;r<size;r++) {
                    Process(baseCube,settings,r/(size-1.0),g/(size-1.0),b/(size-1.0),output);
                    writer.WriteLine(output[0].ToString("0.0000000",CultureInfo.InvariantCulture)+" "+
                        output[1].ToString("0.0000000",CultureInfo.InvariantCulture)+" "+
                        output[2].ToString("0.0000000",CultureInfo.InvariantCulture));
                }
            }
        }

        internal static string ReadRecipe(string path,out FilmLutSettings settings)
        {
            var serializer=new JavaScriptSerializer();
            settings=serializer.Deserialize<FilmLutSettings>(File.ReadAllText(path,Encoding.UTF8));
            if(settings==null)throw new InvalidDataException("Empty film LUT recipe.");
            settings.Validate();
            return settings.ProfileId;
        }

        internal static void WriteRecipe(string path,FilmLutSettings settings)
        {
            settings.Validate();
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(settings),new UTF8Encoding(false));
        }
    }
}
