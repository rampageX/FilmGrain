using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
namespace FilmGrainStudioPreview
{
    internal sealed class NativeZipEntry
    {
        internal string Name; internal long Size,Compressed,Offset; internal uint Crc; internal ushort Method,Flags;
    }
    // ZIP central-directory validation and bounded stored/deflate extraction, compatible with Framework 4.
    internal static class NativeArchiveCore
    {
        internal static string SafeName(string value,HashSet<string> seen)
        {
            string name=value.Replace('\\','/');
            if(string.IsNullOrEmpty(name)||name.StartsWith("/")||name.Contains(":")||name.Contains("//"))throw new InvalidDataException("不安全压缩路径："+value);
            string trimmed=name.TrimEnd('/');
            foreach(string part in trimmed.Split('/')) {
                if(string.IsNullOrEmpty(part)||part=="."||part==".."||part.EndsWith(".")||part.EndsWith(" ")||Regex.IsMatch(part,@"[\x00-\x1f<>\""|?*]")||
                    Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase))throw new InvalidDataException("不安全压缩路径："+value);
            }
            if(!seen.Add(trimmed))throw new InvalidDataException("压缩路径重复："+value);
            return name;
        }
        internal static List<NativeZipEntry> Inspect(string archive,int maxEntries,long totalLimit,long fileLimit)
        {
            using(var f=File.OpenRead(archive))using(var r=new BinaryReader(f)) {
                if(f.Length<22)throw new InvalidDataException("ZIP 过短");
                int length=(int)Math.Min(f.Length,65557);f.Position=f.Length-length;byte[] tail=r.ReadBytes(length);int end=-1;
                for(int n=tail.Length-22;n>=0;n--)if(BitConverter.ToUInt32(tail,n)==0x06054b50&&n+22+BitConverter.ToUInt16(tail,n+20)==tail.Length){end=n;break;}
                if(end<0)throw new InvalidDataException("ZIP 尾部无效");
                int count=BitConverter.ToUInt16(tail,end+10);long central=BitConverter.ToUInt32(tail,end+16),centralSize=BitConverter.ToUInt32(tail,end+12);
                if(BitConverter.ToUInt16(tail,end+4)!=0||BitConverter.ToUInt16(tail,end+6)!=0||BitConverter.ToUInt16(tail,end+8)!=count||count==65535||central==uint.MaxValue||centralSize==uint.MaxValue)
                    throw new InvalidDataException("不支持多卷或 ZIP64 安装包");
                if(count>maxEntries||central+centralSize>f.Length-length+end)throw new InvalidDataException("ZIP 目录范围/条目限制");
                var entries=new List<NativeZipEntry>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;f.Position=central;
                for(int i=0;i<count;i++) {
                    if(r.ReadUInt32()!=0x02014b50)throw new InvalidDataException("ZIP 目录损坏");
                    r.ReadUInt16();r.ReadUInt16();ushort flags=r.ReadUInt16(),method=r.ReadUInt16();r.ReadUInt16();r.ReadUInt16();uint crc=r.ReadUInt32();long compressed=r.ReadUInt32(),size=r.ReadUInt32();
                    int nameLength=r.ReadUInt16(),extra=r.ReadUInt16(),comment=r.ReadUInt16();ushort disk=r.ReadUInt16();r.ReadUInt16();uint attrs=r.ReadUInt32();long offset=r.ReadUInt32();
                    if(disk!=0||(flags&1)!=0||(method!=0&&method!=8)||offset==uint.MaxValue||size==uint.MaxValue||compressed==uint.MaxValue||(attrs>>16&0xf000)==0xa000||(attrs&0x400)!=0)
                        throw new InvalidDataException("ZIP 包含链接、加密或不支持的格式");
                    if(size>fileLimit||compressed>fileLimit||offset>=central||(total+=size)>totalLimit)throw new InvalidDataException("ZIP 文件大小/范围超限");
                    byte[] bytes=r.ReadBytes(nameLength);if(bytes.Length!=nameLength)throw new EndOfStreamException();
                    string name=SafeName(((flags&2048)!=0?new UTF8Encoding(false,true):Encoding.GetEncoding(437)).GetString(bytes),seen);
                    f.Position+=extra+comment;if(f.Position>central+centralSize)throw new InvalidDataException("ZIP 目录越界");
                    if(name.EndsWith("/")&&size!=0)throw new InvalidDataException("ZIP 目录有数据");
                    entries.Add(new NativeZipEntry{Name=name,Size=size,Compressed=compressed,Offset=offset,Crc=crc,Method=method,Flags=flags});
                }
                if(f.Position!=central+centralSize)throw new InvalidDataException("ZIP 目录长度不符");
                var paths=new HashSet<string>(entries.FindAll(x=>!x.Name.EndsWith("/")).ConvertAll(x=>x.Name),StringComparer.OrdinalIgnoreCase);
                foreach(var e in entries) {
                    string parent=e.Name.TrimEnd('/');
                    while(parent.Contains("/")) {parent=parent.Substring(0,parent.LastIndexOf('/'));if(paths.Contains(parent))throw new InvalidDataException("ZIP 文件/目录冲突");}
                }
                return entries;
            }
        }
        internal static void Extract(string archive,string output,IList<NativeZipEntry> entries,Predicate<string> include,CancellationToken token)
        {
            if(Directory.Exists(output)||File.Exists(output))throw new IOException("解压目标已存在："+output);
            Directory.CreateDirectory(output);string prefix=Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            using(var f=File.OpenRead(archive))using(var reader=new BinaryReader(f))foreach(var e in entries) {
                token.ThrowIfCancellationRequested();if(e.Name.EndsWith("/")||!include(e.Name))continue;
                string target=Path.GetFullPath(Path.Combine(output,e.Name.Replace('/',Path.DirectorySeparatorChar)));
                if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("解压路径越界");
                f.Position=e.Offset;if(reader.ReadUInt32()!=0x04034b50)throw new InvalidDataException("ZIP 本地头损坏");
                reader.ReadUInt16();ushort flags=reader.ReadUInt16(),method=reader.ReadUInt16();reader.ReadUInt16();reader.ReadUInt16();reader.ReadUInt32();reader.ReadUInt32();reader.ReadUInt32();
                int nameLength=reader.ReadUInt16(),extra=reader.ReadUInt16();byte[] bytes=reader.ReadBytes(nameLength);
                string local=((flags&2048)!=0?new UTF8Encoding(false,true):Encoding.GetEncoding(437)).GetString(bytes).Replace('\\','/');
                if(local!=e.Name||flags!=e.Flags||method!=e.Method)throw new InvalidDataException("ZIP 本地头与目录不符");
                f.Position+=extra;if(f.Position+e.Compressed>f.Length)throw new InvalidDataException("ZIP 数据越界");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using(var limited=new BoundedReadStream(f,e.Compressed))using(var data=e.Method==8?(Stream)new DeflateStream(limited,CompressionMode.Decompress,true):limited)
                using(var dest=new FileStream(target,FileMode.CreateNew,FileAccess.Write)) {
                    var buffer=new byte[65536];long total=0;uint crc=0xffffffff;int n;
                    while((n=data.Read(buffer,0,buffer.Length))>0) {
                        token.ThrowIfCancellationRequested();total+=n;if(total>e.Size)throw new InvalidDataException("解压长度超限");
                        for(int i=0;i<n;i++)crc=(crc>>8)^CrcTable[(crc^buffer[i])&255];
                        dest.Write(buffer,0,n);
                    }
                    if(total!=e.Size||(~crc)!=e.Crc)throw new InvalidDataException("ZIP 长度或 CRC 校验失败："+e.Name);
                }
            }
        }
        static readonly uint[] CrcTable=MakeCrcTable();
        static uint[] MakeCrcTable(){var table=new uint[256];for(uint n=0;n<256;n++){uint c=n;for(int b=0;b<8;b++)c=(c>>1)^((c&1)!=0?0xedb88320u:0u);table[n]=c;}return table;}
        sealed class BoundedReadStream:Stream
        {
            readonly Stream source;long remaining;internal BoundedReadStream(Stream stream,long length){source=stream;remaining=length;}
            public override int Read(byte[] buffer,int offset,int count){if(remaining==0)return 0;int n=source.Read(buffer,offset,(int)Math.Min(count,remaining));if(n==0)throw new EndOfStreamException();remaining-=n;return n;}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){}public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
        }
    }
}
