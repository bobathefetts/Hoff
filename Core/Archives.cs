using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace MediaReviewer {
    public sealed class ArchiveMember {
        public string Name,Identity; public long Size,ModifiedTicks;public bool IsDirectory;
        internal long Offset,Compressed,DirectoryOffset,ArchiveSize,ArchiveTicks;internal string ArchiveIdentity,Refusal;internal uint Crc;internal ushort Method,Flags;
        internal string Token {get{return String.Join("|",new[]{"ZIP1",ArchiveSize.ToString(CultureInfo.InvariantCulture),ArchiveTicks.ToString(CultureInfo.InvariantCulture),ArchiveIdentity,Offset.ToString(CultureInfo.InvariantCulture),Compressed.ToString(CultureInfo.InvariantCulture),Size.ToString(CultureInfo.InvariantCulture),Crc.ToString(CultureInfo.InvariantCulture),Method.ToString(CultureInfo.InvariantCulture),Flags.ToString(CultureInfo.InvariantCulture),ModifiedTicks.ToString(CultureInfo.InvariantCulture),DirectoryOffset.ToString(CultureInfo.InvariantCulture)});}}
    }
    // A ZIP source is a logical location, never a filesystem extraction destination.
    public static class Archives {
        public const string Separator="|ZIP|";
        internal const long MemberLimit=512L*1024*1024,TotalLimit=4L*1024*1024*1024,DirectoryLimit=64L*1024*1024;
        static readonly SemaphoreSlim expansion=new SemaphoreSlim(1,1);
        static readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
        internal static bool IsPath(string path){return path!=null && path.IndexOf(Separator,StringComparison.Ordinal)>=0;}
        internal static string[] Split(string path){int at=path.IndexOf(Separator,StringComparison.Ordinal);if(at<=0 || path.IndexOf(Separator,at+Separator.Length,StringComparison.Ordinal)>=0)throw new InvalidDataException("Invalid archive source coordinates");return new[]{path.Substring(0,at),Normalize(path.Substring(at+Separator.Length))};}
        internal static string Normalize(string value){
            value=value.Replace('/','\\');if(value.Length==0 || value.Length>4096 || value[0]=='\\' || value.IndexOfAny(new[]{':','|','\0','*','?','"','<','>'})>=0 || value.Any(c=>c<32))throw new InvalidDataException("Unsafe ZIP entry path");
            string[] parts=value.TrimEnd('\\').Split('\\');if(parts.Any(p=>p.Length==0 || p=="." || p==".." || p.EndsWith(".",StringComparison.Ordinal) || p.EndsWith(" ",StringComparison.Ordinal)))throw new InvalidDataException("Unsafe or ambiguous ZIP entry path");
            return String.Join("\\",parts);
        }
        internal static bool Nested(string name){return new[]{".zip",".zipx",".7z",".rar",".tar",".gz",".bz2",".xz"}.Contains(Path.GetExtension(name).ToLowerInvariant());}
        static ushort U16(byte[] b,int at){return BitConverter.ToUInt16(b,at);}static uint U32(byte[] b,int at){return BitConverter.ToUInt32(b,at);}static long U64(byte[] b,int at){ulong n=BitConverter.ToUInt64(b,at);if(n>Int64.MaxValue)throw new InvalidDataException("ZIP64 value exceeds supported bounds");return (long)n;}
        static byte[] Read(FileStream file,int count,CancellationToken token){token.ThrowIfCancellationRequested();byte[] b=new byte[count];int at=0;while(at<count){token.ThrowIfCancellationRequested();int n=file.Read(b,at,count-at);if(n==0)throw new InvalidDataException("Truncated ZIP metadata");at+=n;}return b;}
        // Bounds that contradict the archive mean its directory cannot be trusted, so they fail the whole ZIP.
        static void CheckBounds(ArchiveMember m){if(m.Size<0 || m.Compressed<0 || m.Offset<0 || m.DirectoryOffset<0 || m.DirectoryOffset>m.ArchiveSize || m.Offset>m.DirectoryOffset-30 || m.Compressed>m.DirectoryOffset-m.Offset-30)throw new InvalidDataException("ZIP member bounds are inconsistent with its archive");}
        // Policy limits concern one member only. A refused member is never expanded; the other members stay readable.
        internal static string RefusalFor(ArchiveMember m){
            if(m.Size>MemberLimit || (m.Size+99)/100>m.Compressed || (m.Method!=0 && m.Method!=8) || (m.Flags&~0x080E)!=0 || (m.Method==0 && m.Size!=m.Compressed))return "ZIP member exceeds size/ratio limits or uses encryption/unsupported compression";
            return !m.IsDirectory && Nested(m.Name) ? "Nested archives are excluded; scan an extracted media folder explicitly" : null;
        }
        internal static void Check(ArchiveMember m){CheckBounds(m);string refusal=RefusalFor(m);if(refusal!=null)throw new InvalidDataException(refusal);}
        internal sealed class Directory:IDisposable {
            internal readonly FileStream File;internal readonly FileStamp Stamp;readonly CancellationToken token;long offset,length,count;
            internal Directory(string path,string root,CancellationToken token){this.token=token;File=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.RandomAccess);try{if(root!=null)Paths.OpenedSourceWithinRoot(File,root);Stamp=FileAccessNative.Stamp(File);Header();Validate();}catch{File.Dispose();throw;}}
            void Header(){
                if(Stamp.Size<22)throw new InvalidDataException("ZIP end record is missing");int size=(int)Math.Min(65557,Stamp.Size);File.Position=Stamp.Size-size;byte[] tail=Read(File,size,token);int end=-1;
                for(int i=size-22;i>=0;i--)if(U32(tail,i)==0x06054b50 && i+22+U16(tail,i+20)==size){end=i;break;}if(end<0)throw new InvalidDataException("ZIP end record is invalid");long endAt=Stamp.Size-size+end;
                if(U16(tail,end+4)!=0 || U16(tail,end+6)!=0 || U16(tail,end+8)!=U16(tail,end+10))throw new InvalidDataException("Multi-disk ZIP archives are unsupported");count=U16(tail,end+10);length=U32(tail,end+12);offset=U32(tail,end+16);long boundary=endAt;
                if(count==65535 || length==UInt32.MaxValue || offset==UInt32.MaxValue){
                    if(endAt<20)throw new InvalidDataException("ZIP64 locator is missing");File.Position=endAt-20;byte[] locator=Read(File,20,token);if(U32(locator,0)!=0x07064b50 || U32(locator,4)!=0 || U32(locator,16)!=1)throw new InvalidDataException("Unsupported ZIP64 disk layout");long record=U64(locator,8);if(record>endAt-76)throw new InvalidDataException("Invalid ZIP64 end offset");File.Position=record;byte[] z=Read(File,56,token);
                    if(U32(z,0)!=0x06064b50 || U64(z,4)<44 || U64(z,4)>DirectoryLimit || record+12+U64(z,4)!=endAt-20 || U32(z,16)!=0 || U32(z,20)!=0 || U64(z,24)!=U64(z,32))throw new InvalidDataException("Invalid ZIP64 end record");count=U64(z,32);length=U64(z,40);offset=U64(z,48);boundary=record;
                }
                if(count>10000 || length>DirectoryLimit || offset<0 || offset>boundary || length>boundary-offset || offset+length!=boundary)throw new InvalidDataException("ZIP exceeds directory/count limits or has inconsistent directory bounds");
            }
            internal IEnumerable<ArchiveMember> Members(){
                File.Position=offset;long end=offset+length;for(long i=0;i<count;i++){
                    token.ThrowIfCancellationRequested();if(File.Position>end-46)throw new InvalidDataException("Truncated ZIP central directory");byte[] h=Read(File,46,token);if(U32(h,0)!=0x02014b50)throw new InvalidDataException("Invalid ZIP central-directory signature");int names=U16(h,28),extra=U16(h,30),comment=U16(h,32);if(names==0 || names>4096 || names+extra+comment>end-File.Position)throw new InvalidDataException("ZIP entry metadata exceeds bounds");byte[] raw=Read(File,names,token),extensions=Read(File,extra,token);File.Position+=comment;
                    ushort flags=U16(h,8),method=U16(h,10);string decoded;try{decoded=((flags&2048)!=0 ? (Encoding)utf8 : Encoding.GetEncoding(437)).GetString(raw);}catch(DecoderFallbackException){throw new InvalidDataException("Invalid ZIP entry name encoding");}string name=Normalize(decoded);long expanded=U32(h,24),compressed=U32(h,20),local=U32(h,42);uint disk=U16(h,34);
                    if(expanded==UInt32.MaxValue || compressed==UInt32.MaxValue || local==UInt32.MaxValue || disk==65535){bool found=false;for(int at=0;at+4<=extensions.Length;){int tag=U16(extensions,at),n=U16(extensions,at+2);at+=4;if(n>extensions.Length-at)throw new InvalidDataException("Truncated ZIP extra field");if(tag==1){int next=at;Func<long> take=()=>{if(next>at+n-8)throw new InvalidDataException("Truncated ZIP64 entry");long value=U64(extensions,next);next+=8;return value;};if(expanded==UInt32.MaxValue)expanded=take();if(compressed==UInt32.MaxValue)compressed=take();if(local==UInt32.MaxValue)local=take();if(disk==65535){if(next>at+n-4)throw new InvalidDataException("Truncated ZIP64 disk");disk=U32(extensions,next);}found=true;break;}at+=n;}if(!found)throw new InvalidDataException("ZIP64 entry metadata is missing");}
                    if(disk!=0 || ((U32(h,38)>>16)&0xF000)==0xA000)throw new InvalidDataException("ZIP disk links and symbolic links are unsupported");
                    long ticks=Stamp.ModifiedTicks;try{int date=U16(h,14),time=U16(h,12);ticks=new DateTime(1980+(date>>9),(date>>5)&15,date&31,time>>11,(time>>5)&63,(time&31)*2,DateTimeKind.Local).ToUniversalTime().Ticks;}catch(ArgumentOutOfRangeException){}
                    var member=new ArchiveMember {Name=name,Size=expanded,Compressed=compressed,Offset=local,Crc=U32(h,16),Method=method,Flags=flags,ModifiedTicks=ticks,DirectoryOffset=offset,ArchiveSize=Stamp.Size,ArchiveTicks=Stamp.ModifiedTicks,ArchiveIdentity=Stamp.Identity};CheckBounds(member);
                    member.IsDirectory=decoded.EndsWith("/",StringComparison.Ordinal) || decoded.EndsWith("\\",StringComparison.Ordinal);member.Refusal=RefusalFor(member);member.Identity=member.Token;yield return member;
                }if(File.Position!=end)throw new InvalidDataException("ZIP central-directory count/length mismatch");
            }
            void Validate(){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0,nameBytes=0;foreach(var m in Members()){if(!names.Add(m.Name))throw new InvalidDataException("Ambiguous duplicate ZIP entry paths");if(m.Refusal==null)total=checked(total+m.Size);nameBytes+=m.Name.Length*2L;if(total>TotalLimit || nameBytes>8L*1024*1024)throw new InvalidDataException("ZIP exceeds total expansion or retained-name limits");}Verify();}
            internal void Verify(){if(!Stamp.Same(FileAccessNative.Stamp(File)))throw new FileChangedException("ZIP archive changed during discovery; start a fresh scan");}
            public void Dispose(){File.Dispose();}
        }
        public static List<ArchiveMember> Inspect(string path){using(var directory=new Directory(path,null,CancellationToken.None))return directory.Members().ToList();}
        internal static ArchiveMember Parse(string identity,string name){
            try{string[] p=identity.Split('|');if(p.Length!=12 || p[0]!="ZIP1" || identity.Length>1024)throw new FormatException();var m=new ArchiveMember {Name=Normalize(name),ArchiveSize=Int64.Parse(p[1],CultureInfo.InvariantCulture),ArchiveTicks=Int64.Parse(p[2],CultureInfo.InvariantCulture),ArchiveIdentity=p[3],Offset=Int64.Parse(p[4],CultureInfo.InvariantCulture),Compressed=Int64.Parse(p[5],CultureInfo.InvariantCulture),Size=Int64.Parse(p[6],CultureInfo.InvariantCulture),Crc=UInt32.Parse(p[7],CultureInfo.InvariantCulture),Method=UInt16.Parse(p[8],CultureInfo.InvariantCulture),Flags=UInt16.Parse(p[9],CultureInfo.InvariantCulture),ModifiedTicks=Int64.Parse(p[10],CultureInfo.InvariantCulture),DirectoryOffset=Int64.Parse(p[11],CultureInfo.InvariantCulture)};Check(m);if(m.Token!=identity)throw new FormatException();return m;}catch(Exception e){if(e is FormatException || e is OverflowException || e is NullReferenceException)throw new InvalidDataException("Invalid saved ZIP member identity",e);throw;}
        }
        internal static SourceRead Open(string path,string identity,CancellationToken token,string root){
            string[] parts=Split(path);ArchiveMember m;if(identity==null){using(var directory=new Directory(parts[0],root,token)){m=directory.Members().FirstOrDefault(x=>x.Name==parts[1]);if(m==null)throw new FileNotFoundException("ZIP member is unavailable",path);if(m.Refusal!=null)throw new InvalidDataException(m.Refusal);}}else m=Parse(identity,parts[1]);
            expansion.Wait(token);FileStream file=null;try{
                file=new FileStream(Paths.Io(parts[0]),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);if(root!=null)Paths.OpenedSourceWithinRoot(file,root);var before=FileAccessNative.Stamp(file);if(before.Size!=m.ArchiveSize || before.ModifiedTicks!=m.ArchiveTicks || before.Identity!=m.ArchiveIdentity)throw new InvalidDataException("ZIP archive changed since discovery; start a fresh scan before using this member");
                file.Position=m.Offset;byte[] h=Read(file,30,token);if(U32(h,0)!=0x04034b50 || U16(h,6)!=m.Flags || U16(h,8)!=m.Method)throw new InvalidDataException("ZIP local header differs from its directory");int nameBytes=U16(h,26),extra=U16(h,28);if(nameBytes==0 || nameBytes>4096 || file.Position+nameBytes+extra>m.DirectoryOffset-m.Compressed)throw new InvalidDataException("ZIP local data bounds are invalid");byte[] name=Read(file,nameBytes,token);string localName=((m.Flags&2048)!=0 ? (Encoding)utf8 : Encoding.GetEncoding(437)).GetString(name);if(Normalize(localName)!=m.Name)throw new InvalidDataException("ZIP local entry name differs from its directory");
                if((m.Flags&8)==0 && (U32(h,14)!=m.Crc || (U32(h,18)!=UInt32.MaxValue && U32(h,18)!=m.Compressed) || (U32(h,22)!=UInt32.MaxValue && U32(h,22)!=m.Size)))throw new InvalidDataException("ZIP local sizes/CRC differ from its directory");file.Position+=extra;
                var checkedStream=new MemberStream(file,m,token,()=>expansion.Release());file=null;return new SourceRead(checkedStream,new FileStamp {Size=m.Size,ModifiedTicks=m.ModifiedTicks,Identity=m.Token},checkedStream.Complete);
            }catch{try{if(file!=null)file.Dispose();}finally{expansion.Release();}throw;}
        }
        // sniffExtensions mirrors FileAccessNative.FingerprintScoped: a member whose extension is not a known media
        // type is recognised from its first bytes, so renaming a picture inside a ZIP does not hide it from a scan.
        internal static FileRecord Fingerprint(string path,FileRecord record,byte[] buffer,CancellationToken token,Action<int> progress,long cutoff,string root,string identity=null,string[] sniffExtensions=null){
            using(var source=Open(path,identity,token,root)){if(cutoff>0 && source.Stamp.ModifiedTicks<cutoff)return null;using(var sha=SHA256.Create()){long done=0;int n;
                if(sniffExtensions!=null){
                    var header=new byte[(int)Math.Min(source.Stamp.Size,512)];int read=0;
                    while(read<header.Length){n=source.Stream.Read(header,read,header.Length-read);if(n==0)throw new InvalidDataException("ZIP member ended during header detection");read+=n;}
                    string type=ImageHeader.Sniff(header);if(type==null || !Formats.IncludesDetected(type,sniffExtensions))return null;
                    record.Extension=type;sha.TransformBlock(header,0,read,header,0);done=read;if(progress!=null && read>0)progress(read);
                }
                while((n=source.Stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,n,buffer,0);done+=n;if(progress!=null)progress(n);}source.Validate();sha.TransformFinalBlock(new byte[0],0,0);record.Size=source.Stamp.Size;record.ModifiedTicks=source.Stamp.ModifiedTicks;record.FileIdentity=source.Stamp.Identity;record.BytesRead=done;record.Hash=EvidenceFormat.Hex(sha.Hash);record.Algorithm="sha256";record.AlgorithmVersion=1;record.Sampling="";return record;}}
        }
        internal static PreviewResult Preview(string path,long size,long ticks,string identity,int side,int limit,CancellationToken token,string root){
            return WithPreviewFile(path,size,ticks,identity,token,root,
                (file,stamp)=>MediaReviewer.Preview.CreateExpectedScoped(file,stamp.Size,stamp.ModifiedTicks,stamp.Identity,side,limit,token,Native.RuntimeDirectory));
        }
        internal static T WithPreviewFile<T>(string path,long size,long ticks,string identity,CancellationToken token,string root,Func<string,FileStamp,T> decode){
            // Only a known media extension is carried to the private copy. A renamed member gets none, so the decoder
            // identifies it from its header and no file with an arbitrary extension is ever written to disk.
            string extension=Path.GetExtension(Split(path)[1]);
            string file=MediaScratch.NewArchivePath(Formats.Supported(extension.TrimStart('.').ToLowerInvariant()) ? extension : "");string own=null;
            try{using(var source=SourceRead.Open(path,size,ticks,identity,token,root)){
                if(new DriveInfo(Path.GetPathRoot(file)).AvailableFreeSpace<source.Stamp.Size+64L*1024*1024)throw new IOException("Insufficient private temporary space for this ZIP preview");
                using(var output=new FileStream(Paths.Io(file),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536)){own=FileAccessNative.Stamp(output).Identity;byte[] b=new byte[1024*1024];int n;while((n=source.Stream.Read(b,0,b.Length))>0){token.ThrowIfCancellationRequested();output.Write(b,0,n);}source.Validate();output.Flush();}
            }using(var lease=new FileStream(Paths.Io(file),FileMode.Open,FileAccess.Read,FileShare.Read)){var stamp=FileAccessNative.Stamp(lease);if(stamp.Identity!=own)throw new IOException("Owned archive preview changed");return decode(file,stamp);}}
            finally{if(own!=null)MediaScratch.Release(file,own);}
        }
        sealed class Slice:Stream {
            readonly FileStream file;long remaining;internal Slice(FileStream file,long length){this.file=file;remaining=length;}
            public override int Read(byte[] b,int at,int count){if(remaining==0)return 0;int n=file.Read(b,at,(int)Math.Min(count,remaining));if(n==0)throw new InvalidDataException("Truncated compressed ZIP member");remaining-=n;return n;}
            public override bool CanRead{get{return true;}}public override bool CanWrite{get{return false;}}public override bool CanSeek{get{return false;}}public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}public override void Flush(){}public override long Seek(long a,SeekOrigin b){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class MemberStream:Stream {
            readonly FileStream file;readonly Stream decoded;readonly ArchiveMember m;readonly CancellationToken token;Action release;long read;uint crc=UInt32.MaxValue;bool complete;
            static readonly uint[] crcTable=MakeTable();static uint[] MakeTable(){var t=new uint[256];for(uint i=0;i<t.Length;i++){uint c=i;for(int j=0;j<8;j++)c=(c&1)!=0 ? 0xEDB88320^(c>>1) : c>>1;t[i]=c;}return t;}
            internal MemberStream(FileStream file,ArchiveMember m,CancellationToken token,Action release){this.file=file;this.m=m;this.token=token;this.release=release;var slice=new Slice(file,m.Compressed);decoded=m.Method==8 ? (Stream)new DeflateStream(slice,CompressionMode.Decompress,false) : slice;}
            public override int Read(byte[] b,int offset,int count){token.ThrowIfCancellationRequested();if(count==0)return 0;int n=decoded.Read(b,offset,(int)Math.Min(count,Math.Max(1,m.Size-read+1)));token.ThrowIfCancellationRequested();read+=n;if(read>m.Size || read>MemberLimit)throw new InvalidDataException("ZIP expands beyond its declared size");for(int i=offset;i<offset+n;i++)crc=crcTable[(crc^b[i])&255]^(crc>>8);if(n==0){if(read!=m.Size || (crc^UInt32.MaxValue)!=m.Crc)throw new InvalidDataException("ZIP member length or CRC32 validation failed");if(!new FileStamp {Size=m.ArchiveSize,ModifiedTicks=m.ArchiveTicks,Identity=m.ArchiveIdentity}.Same(FileAccessNative.Stamp(file)))throw new InvalidDataException("ZIP changed while reading its member");complete=true;}return n;}
            internal void Complete(){if(!complete){byte[] one=new byte[1];if(Read(one,0,1)!=0)throw new InvalidDataException("ZIP source was not completely consumed");}}
            public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken cancel){cancel.ThrowIfCancellationRequested();return Task.FromResult(Read(b,o,n));}
            protected override void Dispose(bool disposing){if(release!=null){try{decoded.Dispose();}finally{try{file.Dispose();}finally{var action=release;release=null;action();}}}base.Dispose(disposing);}
            public override bool CanRead{get{return true;}}public override bool CanWrite{get{return false;}}public override bool CanSeek{get{return false;}}public override long Length{get{return m.Size;}}public override long Position{get{return read;}set{throw new NotSupportedException();}}public override void Flush(){}public override long Seek(long a,SeekOrigin b){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
    }
    internal sealed class SourceRead:IDisposable {
        internal readonly Stream Stream;internal readonly FileStamp Stamp;readonly Action validate;
        internal SourceRead(Stream stream,FileStamp stamp,Action validate){Stream=stream;Stamp=stamp;this.validate=validate;}
        internal void Validate(){validate();}public void Dispose(){Stream.Dispose();}
        internal static SourceRead Open(string path,long size,long ticks,string identity,CancellationToken token,string root){
            SourceRead source;if(Archives.IsPath(path))source=Archives.Open(path,identity,token,root);else{var file=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);try{if(root!=null)Paths.OpenedSourceWithinRoot(file,root);var before=FileAccessNative.Stamp(file);source=new SourceRead(file,before,()=>{token.ThrowIfCancellationRequested();if(!before.Same(FileAccessNative.Stamp(file)))throw new FileChangedException("Source changed during reading");});}catch{file.Dispose();throw;}}
            if(size>=0 && (source.Stamp.Size!=size || (identity==null ? source.Stamp.ModifiedTicks/10000!=ticks/10000 : source.Stamp.ModifiedTicks!=ticks || source.Stamp.Identity!=identity))){source.Dispose();throw new InvalidDataException("Source changed before reading; scan its current contents");}return source;
        }
    }
}
