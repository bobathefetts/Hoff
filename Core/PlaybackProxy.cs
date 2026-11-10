using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MediaReviewer {
    internal static class PlaybackProxy {
        const long MaxBytes=256L*1024*1024;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ConvertMedia(byte[] format,byte[] input,byte[] output);

        internal static string Create(string input,string format,long size,CancellationToken token) {
            if(size<1 || size>MaxBytes)throw new InvalidDataException("FLV, WebM and GIF playback copies are limited to 256 MiB of source media.");
            token.ThrowIfCancellationRequested();
            string directory=MediaScratch.EnsureDirectory();
            string output=Path.Combine(directory,"playback-"+Guid.NewGuid().ToString("N")+".wmv");
            bool success=false;
            try {
                string executable;
                using(var current=Process.GetCurrentProcess())executable=current.MainModule.FileName;
                using(var job=new DecoderJob())
                using(var process=new Process {StartInfo=new ProcessStartInfo(executable,"--playback-worker") {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true
                }}) {
                    process.Start();
                    try{job.Assign(process);}catch{process.Kill();process.WaitForExit();throw;}
                    try {
                        using(var request=new BinaryWriter(process.StandardInput.BaseStream,Encoding.UTF8)){
                            request.Write(format);request.Write(input);request.Write(output);
                        }
                        var clock=Stopwatch.StartNew();
                        while(!process.WaitForExit(100)){
                            token.ThrowIfCancellationRequested();
                            if(clock.Elapsed>TimeSpan.FromMinutes(2))throw new IOException("The playback copy took too long to prepare.");
                        }
                    }finally {
                        if(!process.HasExited)job.StopAndWait();
                    }
                    token.ThrowIfCancellationRequested();
                    if(process.ExitCode!=0 || !File.Exists(output))throw new InvalidDataException("The offline decoder could not prepare this FLV, WebM or GIF for playback.");
                    long length=new FileInfo(output).Length;
                    if(length==0 || length>=MaxBytes)throw new InvalidDataException("The playback copy exceeded its 256 MiB limit.");
                    success=true;
                    return output;
                }
            }finally{if(!success)Release(output);}
        }

        internal static int Worker() {
            using(var request=new BinaryReader(WorkerInput.Open(),Encoding.UTF8)){
                string format=request.ReadString(),input=request.ReadString(),output=request.ReadString();
                if(!Formats.UsesPlaybackProxy(format) || !Path.IsPathRooted(input) || !Path.IsPathRooted(output) ||
                    input.Length>32767 || output.Length>32767 || !MediaScratch.IsPlaybackPath(output))return 2;
                using(MediaScratch.PlaybackLease(output))
                    return Native.Export<ConvertMedia>("mcr_playback")(Native.Text(format=="webm" ? "matroska" : format),Native.Text(input),Native.Text(output));
            }
        }

        internal static void Release(string path) {
            if(!String.IsNullOrEmpty(path) && MediaScratch.IsPlaybackPath(path))MediaScratch.Release(path);
        }
    }
}
