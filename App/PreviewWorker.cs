using System;
using System.IO;
using System.Text;
using MediaReviewer;
static class DecoderProgram {
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static string Text(BinaryReader input,int maximum){int length=input.ReadInt32();if(length<0 || length>maximum)throw new InvalidDataException("Decoder request exceeds its bounds");byte[] bytes=input.ReadBytes(length);if(bytes.Length!=length)throw new EndOfStreamException();return Encoding.UTF8.GetString(bytes);}
    static void WriteImage(BinaryWriter output,PreviewResult image){
        byte[] decoder=Encoding.UTF8.GetBytes(image.Decoder);
        if(decoder.Length>2048)throw new InvalidDataException("Decoder label exceeds its byte limit");
        output.Write(image.Width);output.Write(image.Height);output.Write(image.SourceWidth);output.Write(image.SourceHeight);
        output.Write(image.Bytes.Length);output.Write(image.Bytes);output.Write(decoder.Length);output.Write(decoder);
    }
    internal static int Run(){
        try{
            SetErrorMode(0x8003);var input=new BinaryReader(WorkerInput.Open(),Encoding.UTF8);var output=new BinaryWriter(Console.OpenStandardOutput(),Encoding.UTF8);if(input.ReadInt32()!=0x4d435253)throw new InvalidDataException("Decoder parent release message is missing");output.Write(0x4d435244);output.Write(6);output.Flush();
            for(int count=0;count<100;count++){
                int kind;try{kind=input.ReadInt32();}catch(EndOfStreamException){return 0;}
                string path=null,identity=null,root=null;long size=0,ticks=0;int side=256,limit=32768;byte[] saved=null;
                if(kind==0 || kind==2){path=Text(input,131072);size=input.ReadInt64();ticks=input.ReadInt64();identity=Text(input,256);side=input.ReadInt32();limit=input.ReadInt32();root=Text(input,131072);}
                else if(kind==1){int length=input.ReadInt32();if(length<20 || length>32768)throw new InvalidDataException("Saved WebP request exceeds its input limit");saved=input.ReadBytes(length);if(saved.Length!=length)throw new EndOfStreamException();}
                else throw new InvalidDataException("Unknown private decoder request kind");
                PreviewResult[] frames=null;
                try{
                    using(kind==1 ? null : MediaScratch.ArchiveLease(path)){
                        if(kind==2)frames=MediaDecode.CreateMotion(path,size,ticks,identity,root);
                        else frames=new[]{kind==1 ? MediaDecode.CreateSavedWebp(saved) : MediaDecode.CreateScoped(path,size,ticks,identity,side,limit,root)};
                    }
                    output.Write(0);
                    if(kind==2)output.Write(frames.Length);
                    foreach(var image in frames)WriteImage(output,image);
                }
                catch(Exception error){var origin=error.GetBaseException();var native=origin as System.ComponentModel.Win32Exception;int code=native==null ? origin.HResult&65535 : native.NativeErrorCode;byte[] message=Encoding.UTF8.GetBytes(error.ToString());if(message.Length>16384)Array.Resize(ref message,16384);output.Write(1);output.Write(code);output.Write(message.Length);output.Write(message);}
                finally{
                    if(saved!=null)Array.Clear(saved,0,saved.Length);
                    if(frames!=null && kind!=0)foreach(var frame in frames)Array.Clear(frame.Bytes,0,frame.Bytes.Length);
                }
                output.Flush();
            }return 0;
        }catch(Exception error){Console.Error.WriteLine(StartupDiagnostics.Error(error));return 1;}
    }
}
