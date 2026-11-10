// Test harness for the shared native FGS film LUT core (can run via Mono).
// This file is intentionally excluded from the application's production build.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FilmGrainStudioPreview;

internal static class TestFilmLutCore
{
    private static int total;
    private static void Assert(bool condition,string message)
    {total++;if(!condition)throw new InvalidOperationException("FAIL: "+message);}

    private static double Diff(double[] a,double[] b)
    {return Math.Max(Math.Abs(a[0]-b[0]),Math.Max(Math.Abs(a[1]-b[1]),Math.Abs(a[2]-b[2])));}

    private static void Main(string[] args)
    {
        if(args.Length<1||args.Length>2)throw new ArgumentException("Expected repository root [optional sample output CUBE]");
        string root=args[0],temp=Path.Combine(Path.GetTempPath(),"FGS_FilmLutTest_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try {
            foreach(var profile in FilmLutCore.Profiles) {
                var cube=FilmLutCube.Read(FilmLutCore.ProfilePath(root,profile.Id));
                Assert(cube.Size==33,"profile size " + profile.Id);
                var defaults=FilmLutSettings.Default(profile.Id);
                var plain=new double[3];var changed=new double[3];
                for(int r=0;r<=24;r+=3)for(int g=1;g<=25;g+=4)for(int b=0;b<=26;b+=13) {
                    double red=r/25.0,green=g/26.0,blue=b/27.0;
                    cube.Sample(red,green,blue,plain);
                    FilmLutCore.Process(cube,defaults,red,green,blue,changed);
                    Assert(Diff(plain,changed)<1e-12,"default editor preserves spectral cube");
                    Assert(changed[0]>=0&&changed[0]<=1&&changed[1]>=0&&changed[1]<=1&&changed[2]>=0&&changed[2]<=1,"SDR range");
                }
                string file=Path.Combine(temp,profile.Id+".cube");
                FilmLutCore.WriteCube(file,cube,defaults);
                var generated=FilmLutCube.Read(file);
                for(int n=0;n<=32;n+=4) {
                    double channel=n/32.0;
                    cube.Sample(channel,channel,channel,plain);
                    generated.Sample(channel,channel,channel,changed);
                    Assert(Diff(plain,changed)<2e-7,"default export parity " + profile.Id);
                }
                var settings=new FilmLutSettings{ProfileId=profile.Id,ExposureTenths=3,Warmth=15,Saturation=118,Strength=67};
                var copy=settings.Copy();
                Assert(copy.ProfileId==settings.ProfileId&&copy.Strength==67&&
                    !object.ReferenceEquals(copy,settings),"independent recipe copy");
                string session=FilmLutCore.WriteSessionCube(root,settings);
                Assert(File.Exists(session),"unsaved Apply writes usable session LUT");
                string fileName=Path.GetFileName(session);
                string stockPrefix=profile.English.Replace(" · ","_").Replace(' ','_')+"_";
                Assert(fileName.StartsWith(stockPrefix,StringComparison.Ordinal),"Apply LUT names the original film stock");
                Assert(Regex.IsMatch(fileName.Substring(stockPrefix.Length),"^[0-9a-f]{8}\\.cube$"),"Apply LUT uses eight hex digits");
                Assert(!string.Equals(fileName,FilmLutCore.NewCubeFileName(profile.Id),StringComparison.Ordinal),"new LUT filename is randomized");
                var sessionCube=FilmLutCube.Read(session);
                var fullStrength=settings.Copy();fullStrength.Strength=100;
                for(int c=0;c<=32;c+=8){
                    var pixel=c/32.0;
                    sessionCube.Sample(pixel,pixel,pixel,plain);
                    FilmLutCore.Process(cube,fullStrength,pixel,pixel,pixel,changed);
                    Assert(Diff(plain,changed)<2e-7,"Apply LUT uses 100% stock; outer LUT strength controls blending");
                }
                Assert(settings.Strength==67,"session rendering does not mutate recipe strength");
                string recipe=Path.Combine(temp,profile.Id+".film.json");
                FilmLutCore.WriteRecipe(recipe,settings);
                FilmLutSettings loaded;FilmLutCore.ReadRecipe(recipe,out loaded);
                Assert(loaded.ProfileId==profile.Id&&loaded.ExposureTenths==3&&loaded.Warmth==15&&
                    loaded.Saturation==118&&loaded.Strength==67,"recipe roundtrip");
                var cached=new FilmLutCore.PreviewTransform(settings);
                for(int r=0;r<=255;r+=17)for(int g=0;g<=255;g+=31)for(int b=0;b<=255;b+=53) {
                    FilmLutCore.ProcessByte(cube,cached,(byte)r,(byte)g,(byte)b,plain);
                    FilmLutCore.Process(cube,settings,r/255.0,g/255.0,b/255.0,changed);
                    Assert(Diff(plain,changed)<1e-12,"preview parity");
                }
                var output=new double[3];var sw=Stopwatch.StartNew();
                for(int i=0;i<800*450;i++)FilmLutCore.ProcessByte(cube,cached,
                    (byte)(i%256),(byte)((i*5)%256),(byte)((i*7)%256),output);
                sw.Stop();
                Console.WriteLine("PASS: " + profile.Id + ", 800x450 preview core " +
                    sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            }
            bool rejected=false;
            try{FilmLutCore.ProfilePath(root,"../Invalid");}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"reject arbitrary recipe profile paths");
            if(args.Length==2) {
                var cube=FilmLutCube.Read(FilmLutCore.ProfilePath(root,"Vision3_250D_2383"));
                var adjusted=new FilmLutSettings{ProfileId="Vision3_250D_2383",ExposureTenths=2,
                    Warmth=12,Saturation=108,Strength=85};
                FilmLutCore.WriteCube(args[1],cube,adjusted);
                Console.WriteLine("PASS: adjusted CUBE exported to "+args[1]);
            }
            Console.WriteLine("PASS: "+total+" numeric/validation assertions");
        }finally{try{Directory.Delete(temp,true);}catch{}}
    }
}
