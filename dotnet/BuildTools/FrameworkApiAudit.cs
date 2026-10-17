using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal sealed class FrameworkOnlyResolver : IAssemblyResolver
{
    private readonly string[] roots;
    private readonly bool requireReferenceAssemblies;
    private readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
    internal FrameworkOnlyResolver(string[] roots, bool requireReferenceAssemblies) { this.roots=roots; this.requireReferenceAssemblies=requireReferenceAssemblies; }
    internal void Register(AssemblyDefinition assembly) { cache[assembly.Name.Name]=assembly; }
    public AssemblyDefinition Resolve(AssemblyNameReference name) { return Resolve(name,new ReaderParameters()); }
    public AssemblyDefinition Resolve(AssemblyNameReference name,ReaderParameters unused)
    {
        AssemblyDefinition value;
        if(cache.TryGetValue(name.Name,out value))return value;
        foreach(string root in roots)
        {
            string path=Path.Combine(root,name.Name+".dll");
            if(!File.Exists(path))continue;
            value=AssemblyDefinition.ReadAssembly(path,new ReaderParameters{AssemblyResolver=this});
            if(!string.Equals(value.Name.Name,name.Name,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Wrong framework assembly: "+path);
            if(requireReferenceAssemblies&&!value.CustomAttributes.Any(x=>x.AttributeType.FullName=="System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
            { value.Dispose();throw new InvalidDataException("Microsoft reference assembly required; implementation libraries rejected: "+path); }
            cache[name.Name]=value;return value;
        }
        // Never fall back to Mono, the current runtime, the application's directory or NuGet.
        throw new FileNotFoundException("Framework-only resolver cannot find "+name.Name+".dll");
    }
    public void Dispose() { foreach(var value in cache.Values)value.Dispose(); cache.Clear(); }
}

internal static class FrameworkApiAudit
{
    private static bool ValidArrayIntrinsic(MethodReference method)
    {
        var array=method.DeclaringType as ArrayType;
        if(array==null||!method.HasThis)return false;
        int rank=array.Rank;
        bool returnsVoid=method.ReturnType.FullName=="System.Void";
        int indexCount=method.Parameters.Count;
        if(method.Name==".ctor")
        { if(!returnsVoid||(indexCount!=rank&&indexCount!=rank*2))return false; }
        else if(method.Name=="Get")
        { if(indexCount!=rank||method.ReturnType.FullName!=array.ElementType.FullName)return false; }
        else if(method.Name=="Address")
        { var byref=method.ReturnType as ByReferenceType;if(indexCount!=rank||byref==null||byref.ElementType.FullName!=array.ElementType.FullName)return false; }
        else if(method.Name=="Set")
        { if(!returnsVoid||indexCount!=rank+1||method.Parameters[indexCount-1].ParameterType.FullName!=array.ElementType.FullName)return false;indexCount--; }
        else return false;
        for(int i=0;i<indexCount;i++)if(method.Parameters[i].ParameterType.FullName!="System.Int32")return false;
        return true; // ECMA-335 array intrinsics are provided by CLR, not mscorlib method rows.
    }

    private static Version FourParts(string value)
    {
        Version v = new Version(value);
        return new Version(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    }
    private static void CheckProductMetadata(AssemblyDefinition candidate,string path)
    {
        var program=candidate.MainModule.GetType("FilmGrainStudioPreview.Program");
        if(program==null)throw new InvalidDataException("Product Program type missing for metadata validation.");
        var version=program.Fields.FirstOrDefault(f=>f.Name=="ReleaseVersion" && f.HasConstant);
        if(version==null)throw new InvalidDataException("Product ReleaseVersion constant missing.");
        string release=Convert.ToString(version.Constant,System.Globalization.CultureInfo.InvariantCulture);
        Version expected=FourParts(release);
        var fixedVersion=program.Fields.FirstOrDefault(f=>f.Name=="FileReleaseVersion" && f.HasConstant);
        if(fixedVersion==null || Convert.ToString(fixedVersion.Constant)!=expected.ToString())throw new InvalidDataException("Fixed file version does not match release version.");
        if(candidate.Name.Version!=expected)throw new InvalidDataException("Assembly version differs from ReleaseVersion.");
        var product=candidate.CustomAttributes.FirstOrDefault(a=>a.AttributeType.FullName=="System.Reflection.AssemblyProductAttribute");
        var file=candidate.CustomAttributes.FirstOrDefault(a=>a.AttributeType.FullName=="System.Reflection.AssemblyFileVersionAttribute");
        var info=candidate.CustomAttributes.FirstOrDefault(a=>a.AttributeType.FullName=="System.Reflection.AssemblyInformationalVersionAttribute");
        if(product==null || Convert.ToString(product.ConstructorArguments[0].Value)!="FilmGrain Studio" || file==null || FourParts(Convert.ToString(file.ConstructorArguments[0].Value))!=expected || info==null || Convert.ToString(info.ConstructorArguments[0].Value)!=release)
            throw new InvalidDataException("Managed product/file/informational version metadata missing or incorrect.");
        var native=System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.GetFullPath(path));
        if(native.ProductName!="FilmGrain Studio" || native.FileDescription!="FilmGrain Studio" || native.FileVersion!=expected.ToString() || native.ProductVersion!=release || new Version(native.FileMajorPart,native.FileMinorPart,native.FileBuildPart,native.FilePrivatePart)!=expected)
            throw new InvalidDataException("Windows VERSIONINFO product/name/version missing or incorrect.");
        Console.WriteLine("Product metadata: "+native.ProductName+"; FileDescription="+native.FileDescription+"; FileVersion="+native.FileVersion+"; ProductVersion="+native.ProductVersion+"; AssemblyVersion="+candidate.Name.Version);
    }

    public static int Main(string[] args)
    {
        try
        {
            if(args.Length<2)throw new ArgumentException("Usage: FrameworkApiAudit.exe product.exe reference-directory | --windows-runtime");
            bool windows=args[1]=="--windows-runtime";
            string[] roots;
            if(windows)
            {
                if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new InvalidOperationException("Runtime audit requires Windows .NET Framework; Mono is forbidden.");
                roots=new string[]{typeof(object).Assembly.Location,typeof(Uri).Assembly.Location,typeof(Enumerable).Assembly.Location,
                    typeof(System.Drawing.Color).Assembly.Location,typeof(System.Windows.Forms.Control).Assembly.Location,
                    typeof(System.Web.Script.Serialization.JavaScriptSerializer).Assembly.Location}.Select(Path.GetDirectoryName).Distinct().ToArray();
            }
            else roots=new string[]{Path.GetFullPath(args[1])};
            using(var resolver=new FrameworkOnlyResolver(roots,!windows))
            {
                var framework=resolver.Resolve(new AssemblyNameReference("mscorlib",new Version(4,0,0,0)));
                if(framework.Name.Name!="mscorlib"||framework.Name.Version.Major!=4)throw new InvalidDataException("Expected Framework mscorlib v4.");
                if(!windows&&!framework.CustomAttributes.Any(x=>x.AttributeType.FullName=="System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
                    throw new InvalidDataException("Microsoft Framework reference assemblies required; implementation libraries rejected.");
                using(var candidate=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver}))
                {
                    CheckProductMetadata(candidate,args[0]);
                    resolver.Register(candidate);
                    int checks=0,failures=0;
                    foreach(var module in candidate.Modules)
                    {
                        foreach(var type in module.GetTypeReferences())
                        {
                            try { if(type.Resolve()==null)throw new MissingMemberException();checks++; }
                            catch(Exception ex){failures++;Console.Error.WriteLine("MISSING TYPE: "+type.FullName+" / "+ex.Message);}
                        }
                        foreach(var member in module.GetMemberReferences())
                        {
                            try
                            {
                                var method=member as MethodReference;var field=member as FieldReference;
                                if(method!=null&&!ValidArrayIntrinsic(method)&&method.Resolve()==null)throw new MissingMethodException();
                                if(field!=null&&field.Resolve()==null)throw new MissingFieldException();
                                checks++;
                            }
                            catch(Exception ex){failures++;Console.Error.WriteLine("MISSING API: "+member.FullName+" / "+ex.Message);}
                        }
                    }
                    Console.WriteLine("Framework API audit: "+checks+" resolved, "+failures+" failures; "+(windows?"physical Windows Framework":"Microsoft reference assemblies")+".");
                    return failures==0?0:1;
                }
            }
        }
        catch(Exception ex){Console.Error.WriteLine("AUDIT STOP: "+ex.Message);return 2;}
    }
}
