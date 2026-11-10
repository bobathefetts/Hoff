using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaReviewer {
    internal sealed class DraftModelWorker:IDraftClassifier {
        internal const string FileName="Marqo-NSFW-384.onnx";
        internal const string GemmaFileName="gemma-4-E2B-it-Q4_0.gguf",ProjectorFileName="mmproj-gemma-4-E2B-it-Q8_0.gguf";
        const string GemmaSha="8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52",ProjectorSha="9406f99c16d68cda4f1f0552192dcc99021ea1fc6d2fd50b1dc3ccf30d04b292";
        internal const string Sha256="2e0ead404a2527e4f5f81f053519f8d315c9ba8db42ffdfc3309f08061bfe8ba";
        const int Release=0x4d435241,Ready=0x4d435331,MaxPreview=32768;
        Process process;
        DecoderJob job;
        BinaryReader input;
        BinaryWriter output;
        bool started;
        readonly bool gemma;
        readonly string criteria;
        bool tryGpu, fellBack;
        public string Device {get;private set;}

        internal DraftModelWorker(bool useGemma=false,bool cpuOnly=false,string criteria=""){gemma=useGemma;tryGpu=gemma && !cpuOnly;this.criteria=criteria;}

        internal static string ModelPath(string executable){return Path.Combine(Path.GetDirectoryName(executable),FileName);}
        internal static bool GemmaCpuSupported(){return Native.Export<CpuSupported>("mcr_gemma_cpu_supported")()!=0;}
        internal static bool GemmaAvailable(){
            if(!GemmaCpuSupported())return false;
            try{
                string directory=ExecutableDirectory();
                CheckLocalFile(Path.Combine(directory,GemmaFileName));CheckLocalFile(Path.Combine(directory,ProjectorFileName));
                return true;
            }catch(IOException){return false;}
            catch(InvalidOperationException){return false;}
            catch(UnauthorizedAccessException){return false;}
        }
        static string ExecutableDirectory(){using(var current=Process.GetCurrentProcess())return Path.GetDirectoryName(current.MainModule.FileName);}

        void Start(CancellationToken token){
            string executable;
            using(var current=Process.GetCurrentProcess())executable=current.MainModule.FileName;
            job=gemma ? new DecoderJob(6144,1) : new DecoderJob(384);
            string arguments=gemma ? (tryGpu ? "--draft-worker gemma-vulkan" : "--draft-worker gemma") : "--draft-worker";
            process=new Process {StartInfo=new ProcessStartInfo(executable,arguments) {UseShellExecute=false,CreateNoWindow=true,
                WorkingDirectory=Environment.SystemDirectory,RedirectStandardInput=true,RedirectStandardOutput=true}};
            try{
                process.Start();started=true;job.Assign(process);
                input=new BinaryReader(process.StandardOutput.BaseStream,Encoding.UTF8);
                output=new BinaryWriter(process.StandardInput.BaseStream,Encoding.UTF8);
                output.Write(Release);
                if(gemma){
                    byte[] rubric=Encoding.UTF8.GetBytes(String.IsNullOrEmpty(criteria) ? GemmaCriteria.Default : criteria);
                    output.Write(rubric.Length);output.Write(rubric);
                }
                output.Flush();
                Device=Wait(Task.Run(()=>{
                    int response=input.ReadInt32();
                    if(response==-1)throw new InvalidDataException(ReadError(input));
                    if(response!=Ready)throw new InvalidDataException("Unexpected AI worker handshake.");
                    return ReadError(input);
                }),token,gemma ? 180000 : 60000);
                if(fellBack)Device+=" (Vulkan fallback)";
            }catch{Dispose();throw;}
        }

        internal void Prepare(CancellationToken token){
            token.ThrowIfCancellationRequested();
            if(process!=null)return;
            try{Start(token);}
            catch(Exception error){
                if(!CanFallBack(error,token))throw;
                UseCpu();
                Start(token);
            }
        }

        bool CanFallBack(Exception error,CancellationToken token){
            return tryGpu && !token.IsCancellationRequested && (error is IOException || error is InvalidDataException);
        }
        void UseCpu(){Dispose();tryGpu=false;fellBack=true;}

        public double? Score(byte[] preview,CancellationToken token){
            if(preview==null || preview.Length<1 || preview.Length>MaxPreview)throw new ArgumentException("Invalid saved preview length.");
            token.ThrowIfCancellationRequested();
            try{return ScoreOnCurrentDevice(preview,token);}
            catch(Exception error){
                if(!CanFallBack(error,token))throw;
                UseCpu();
                return ScoreOnCurrentDevice(preview,token);
            }
        }

        double? ScoreOnCurrentDevice(byte[] preview,CancellationToken token){
            Prepare(token);
            return Wait(Task.Run<double?>(()=>{
                output.Write(preview.Length);output.Write(preview);output.Flush();
                int response=input.ReadInt32();
                if(response==1)return null;
                if(response==-1)throw new InvalidDataException(ReadError(input));
                if(response!=0)throw new InvalidDataException("Unexpected AI worker score response.");
                return input.ReadDouble();
            }),token,gemma ? 180000 : 30000);
        }

        T Wait<T>(Task<T> task,CancellationToken token,int timeout){
            var clock=Stopwatch.StartNew();
            while(!task.IsCompleted){
                if(!token.WaitHandle.WaitOne(25) && clock.ElapsedMilliseconds<timeout)continue;
                job.StopAndWait();
                try{task.GetAwaiter().GetResult();}catch{}
                token.ThrowIfCancellationRequested();
                throw new IOException("The private AI worker exceeded its time limit and was stopped.");
            }
            try{return task.GetAwaiter().GetResult();}
            catch(EndOfStreamException){throw new IOException("The private AI worker closed unexpectedly. Check the approved endpoint logs for a block or process failure.");}
        }

        public void Dispose(){
            if(job!=null){job.StopAndWait();job.Dispose();job=null;}
            if(process==null)return;
            if(started){if(!process.HasExited){try{process.Kill();}catch(InvalidOperationException){}}process.WaitForExit();}
            if(input!=null)input.Dispose();if(output!=null)output.Dispose();
            process.Dispose();process=null;input=null;output=null;started=false;
        }

        static string ReadError(BinaryReader reader){return ReadText(reader,8192);}
        static string ReadText(BinaryReader reader,int maxBytes){
            int size=reader.ReadInt32();
            if(size<1 || size>maxBytes)throw new InvalidDataException("Invalid AI worker text frame.");
            byte[] message=reader.ReadBytes(size);
            if(message.Length!=size)throw new EndOfStreamException();
            return Encoding.UTF8.GetString(message);
        }

        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr OpenModel(byte[] bytes,UIntPtr length,byte[] error,UIntPtr errorLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CpuSupported();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FindGpu(byte[] error,UIntPtr errorLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr OpenGemma(byte[] weights,byte[] projector,IntPtr gpu,byte[] criteria,byte[] error,UIntPtr errorLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RunModel(IntPtr model,byte[] pixels,int width,int height,out double score,byte[] error,UIntPtr errorLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CloseModel(IntPtr model);

        internal static int Run(bool gemma,bool useGpu){
            SetErrorMode(0x8003);
            using(var reader=new BinaryReader(WorkerInput.Open(),Encoding.UTF8))
            using(var writer=new BinaryWriter(Console.OpenStandardOutput(),Encoding.UTF8)){
                IntPtr model=IntPtr.Zero;CloseModel close=null;
                try{
                    if(reader.ReadInt32()!=Release)throw new InvalidDataException("Missing AI worker parent handshake.");
                    string directory=ExecutableDirectory(),prefix=gemma ? "mcr_gemma_" : "mcr_draft_";
                    byte[] error=new byte[8192];
                    var run=Native.Export<RunModel>(prefix+"score");close=Native.Export<CloseModel>(prefix+"close");
                    if(gemma){
                        if(!GemmaCpuSupported())throw new InvalidDataException("Gemma requires AVX2, FMA and F16C enabled on this computer or VM.");
                        string rubric=GemmaCriteria.Prompt(GemmaCriteria.Normalize(ReadText(reader,GemmaCriteria.Limit*4)));
                        IntPtr gpu=IntPtr.Zero;
                        if(useGpu){
                            gpu=Native.Export<FindGpu>("mcr_gemma_find_gpu")(error,(UIntPtr)error.Length);
                            if(gpu==IntPtr.Zero)throw new InvalidDataException(ErrorText(error));
                        }
                        string weights=Path.Combine(directory,GemmaFileName),projector=Path.Combine(directory,ProjectorFileName);
                        using(var modelLease=VerifiedGemmaFile(weights,GemmaSha))
                        using(var projectorLease=VerifiedGemmaFile(projector,ProjectorSha))
                            model=Native.Export<OpenGemma>(prefix+"open")(Native.Text(weights),Native.Text(projector),gpu,Native.Text(rubric),error,(UIntPtr)error.Length);
                    }else{
                        byte[] bytes=LoadModel(Path.Combine(directory,FileName));
                        model=Native.Export<OpenModel>(prefix+"open")(bytes,(UIntPtr)bytes.Length,error,(UIntPtr)error.Length);
                    }
                    if(model==IntPtr.Zero)throw new InvalidDataException(ErrorText(error));
                    byte[] device=Encoding.UTF8.GetBytes(gemma ? ErrorText(error) : "CPU");
                    writer.Write(Ready);writer.Write(device.Length);writer.Write(device);writer.Flush();
                    while(true){
                        int length;try{length=reader.ReadInt32();}catch(EndOfStreamException){return 0;}
                        if(length<1 || length>MaxPreview)throw new InvalidDataException("Invalid AI preview frame size.");
                        byte[] preview=reader.ReadBytes(length);
                        if(preview.Length!=length)throw new EndOfStreamException();
                        BitmapSource image=DecodePreview(preview);
                        if(image==null){writer.Write(1);writer.Flush();continue;}
                        int stride=image.PixelWidth*3;byte[] pixels=new byte[stride*image.PixelHeight];image.CopyPixels(pixels,stride,0);
                        double score;
                        if(run(model,pixels,image.PixelWidth,image.PixelHeight,out score,error,(UIntPtr)error.Length)!=0)throw new InvalidDataException(ErrorText(error));
                        writer.Write(0);writer.Write(score);writer.Flush();
                    }
                }catch(Exception e){
                    byte[] message=Encoding.UTF8.GetBytes(e.GetBaseException().Message);
                    int size=Math.Min(message.Length,8192);writer.Write(-1);writer.Write(size);writer.Write(message,0,size);writer.Flush();return 1;
                }finally{if(model!=IntPtr.Zero)close(model);}
            }
        }

        static BitmapSource DecodePreview(byte[] preview){
            try{
                if(ImageHeader.Detect(preview,"")=="webp")preview=MediaDecode.CreateSavedWebp(preview).Bytes;
                return new FormatConvertedBitmap(MediaLoader.Decode(preview,256),PixelFormats.Bgr24,null,0);
            }catch(ArgumentException){return null;}
            catch(InvalidDataException){return null;}
            catch(IOException){return null;}
            catch(NotSupportedException){return null;}
            catch(FormatException){return null;}
        }

        static string ErrorText(byte[] error){int length=Array.IndexOf(error,(byte)0);return Encoding.UTF8.GetString(error,0,length<0 ? error.Length : length);}

        static void CheckLocalFile(string file){
            string root=Path.GetPathRoot(file);
            if(root.Length!=3 || root[1]!=':' || new DriveInfo(root).DriveType!=DriveType.Fixed)
                throw new InvalidOperationException("Run FleetHacker and its model file from a local fixed drive for AI draft review.");
            string item=root;
            foreach(string component in file.Substring(root.Length).Split(Path.DirectorySeparatorChar)){
                item=Path.Combine(item,component);
                try{if((File.GetAttributes(Paths.Io(item))&(FileAttributes.ReparsePoint|FileAttributes.Offline))!=0)throw new InvalidOperationException("AI draft review requires a local folder and model file without offline or redirected files.");}
                catch(FileNotFoundException){throw new FileNotFoundException("Place the approved "+Path.GetFileName(file)+" next to FleetHacker.exe.");}
            }
        }

        static FileStream VerifiedGemmaFile(string file,string expected){
            CheckLocalFile(file);
            var stream=new FileStream(Paths.Io(file),FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan);
            try{
                if(stream.Length<1024 || stream.Length>4L*1024*1024*1024)throw new InvalidDataException("Unexpected size for "+Path.GetFileName(file)+".");
                using(var sha=SHA256.Create()){
                    string actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                    if(!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The file "+Path.GetFileName(file)+" does not match the approved Gemma weights.");
                }
                return stream;
            }catch{stream.Dispose();throw;}
        }

        static byte[] LoadModel(string file){
            CheckLocalFile(file);
            using(var model=new FileStream(Paths.Io(file),FileMode.Open,FileAccess.Read,FileShare.Read)){
                const int size=22472568;
                if(model.Length!=size)throw new InvalidDataException("The model file does not match the approved Marqo export.");
                byte[] bytes=new byte[size];int offset=0;
                while(offset<size){int read=model.Read(bytes,offset,size-offset);if(read==0)throw new EndOfStreamException();offset+=read;}
                using(var sha=SHA256.Create()){
                    string actual=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
                    if(!String.Equals(actual,Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The model file does not match the approved Marqo export.");
                }
                return bytes;
            }
        }
    }
}
