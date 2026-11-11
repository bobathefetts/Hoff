using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MediaReviewer {
    static class CommandLine {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int id);

        internal static bool IsHeadless(string[] args) {
            if(args.Length==0)return false;
            return args[0]=="--help" || args[0]=="--verify-dependencies" || args[0]=="--verify-draft-model" || args[0]=="--verify-gemma-model" || args[0]=="--create-database" || args[0]=="--import-html" || (args[0]=="--database" && args.Length>2);
        }

        static void AttachIfNeeded() {
            var output=GetStdHandle(-11);var error=GetStdHandle(-12);
            if((output==IntPtr.Zero || output==new IntPtr(-1)) && (error==IntPtr.Zero || error==new IntPtr(-1)))AttachConsole(UInt32.MaxValue);
            UseBomFreeConsoleInput();
        }

        // Process.Start writes the console input encoding's byte order mark into a redirected child stdin. In a
        // UTF-8 console (code page 65001) that mark arrives ahead of the private worker handshake, so the preview,
        // playback and AI workers all reject their parent. The same code page without a mark avoids it.
        static void UseBomFreeConsoleInput() {
            try {
                var input=Console.InputEncoding;
                if(input.CodePage==65001 && input.GetPreamble().Length>0)Console.InputEncoding=new System.Text.UTF8Encoding(false);
            } catch(IOException) {
            } catch(System.Security.SecurityException) {
            }
        }

        internal static int Run(string[] args) {
            AttachIfNeeded();
            if(args.Length==1 && args[0]=="--help"){
                Console.WriteLine("Media Compliance Reviewer\n--database PATH\n--create-database PATH\n--import-html SOURCE DESTINATION\n--open-evidence-package PATH\n--verify-dependencies\n--verify-gemma-model [--cpu-only] IMAGE [IMAGE ...]\n--database PATH --scan-root ROOT [--workers N] [--scan-mode quick|deep] [--extensions jpg,png] [--scan-archives] [--no-sniff-renamed] [--full-hash] [--include-appdata] [--exclude-root-files] [--max-age-years N]\n--database PATH --resume-scan ID|latest [--workers N]");
                return 0;
            }
            if(args.Length==1 && args[0]=="--verify-dependencies"){VerifyDependencies();return 0;}
            if(args.Length==1 && args[0]=="--verify-draft-model"){
                byte[] image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/3Z8AAAAASUVORK5CYII=");
                using(var worker=new DraftModelWorker()){
                    double? score=worker.Score(image,CancellationToken.None);
                    if(!score.HasValue || Double.IsNaN(score.Value) || score<0 || score>1 || worker.Score(new byte[]{0,1,2},CancellationToken.None)!=null || worker.Score(image,CancellationToken.None)!=score)
                        throw new InvalidDataException("Unexpected AI model fixture result.");
                    Console.WriteLine("Verified local Marqo draft worker. Generated fixture score "+score.Value.ToString("R",CultureInfo.InvariantCulture)+".");
                }
                return 0;
            }
            if(args[0]=="--verify-gemma-model"){
                bool cpuOnly=args.Length>1 && args[1]=="--cpu-only";
                int first=cpuOnly ? 2 : 1;
                if(args.Length-first<1 || args.Length-first>20)throw new ArgumentException("Supply 1 to 20 local images for Gemma verification.");
                using(var worker=new DraftModelWorker(true,cpuOnly)){
                    string device=null;
                    for(int i=first;i<args.Length;i++){
                        double? score=worker.Score(Preview.Create(args[i]).Bytes,CancellationToken.None);
                        int fixture=i-first+1;
                        if(!score.HasValue)throw new InvalidDataException("Fixture "+fixture+" could not be scored.");
                        if(device!=worker.Device){device=worker.Device;Console.WriteLine("Gemma device: "+device);}
                        Console.WriteLine("Fixture "+fixture+": "+score.Value.ToString("P0",CultureInfo.InvariantCulture));
                    }
                }
                return 0;
            }
            if(args.Length==2 && args[0]=="--create-database"){
                using(var engine=ReviewerEngine.Open(args[1],true)){AwaitReady(engine);Console.WriteLine("Created catalog · "+engine.DatabasePath);}return 0;
            }
            if(args.Length==3 && args[0]=="--import-html"){
                using(var engine=ReviewerEngine.ImportHtml(args[1],args[2])){AwaitReady(engine);Console.WriteLine("Imported HTML catalog · "+engine.DatabasePath);}return 0;
            }
            if(args.Length<4 || args[0]!="--database")throw new ArgumentException("Invalid command. Use --help for supported arguments.");
            var options=ParseScan(args.Skip(2).ToArray());
            using(var engine=ReviewerEngine.Open(args[1],false)){
                AwaitReady(engine);
                int cancelled=0;
                ConsoleCancelEventHandler handler=(sender,e)=>{e.Cancel=true;Interlocked.Exchange(ref cancelled,1);};
                Console.CancelKeyPress+=handler;
                try {
                    if(options.Resume==null)Console.WriteLine("Scan · "+engine.StartScan(options.Scan));
                    else {
                        string id=options.Resume=="latest" ? engine.Status.ResumeId : options.Resume;
                        if(String.IsNullOrEmpty(id))throw new InvalidOperationException("No interrupted scan is available in this catalog.");
                        engine.ResumeScan(id,options.Scan.WorkerCount);Console.WriteLine("Resuming scan · "+id);
                    }
                    var elapsed=Stopwatch.StartNew();long next=0;bool requested=false;
                    while(engine.Status.RunningScan && !engine.Faulted){
                        if(Volatile.Read(ref cancelled)!=0 && !requested){requested=true;engine.CancelScan();}
                        var state=engine.Status;
                        if(elapsed.ElapsedMilliseconds>=next){Console.WriteLine(state.Stage+" · "+state.ProcessedFiles+"/"+state.DiscoveredFiles+" files · "+state.FailedFiles+" errors");next=elapsed.ElapsedMilliseconds+1000;}
                        Thread.Sleep(100);
                    }
                    var result=engine.Status;Console.WriteLine(result.Stage);
                    if(engine.Faulted || !String.IsNullOrEmpty(result.Error))throw new IOException(String.IsNullOrEmpty(result.Error) ? "The database stopped during the scan." : result.Error);
                    return requested ? 2 : result.CompletedWithErrors ? 3 : String.IsNullOrEmpty(result.ResumeId) ? 0 : 1;
                }finally{Console.CancelKeyPress-=handler;}
            }
        }

        sealed class ScanArguments {public ScanOptions Scan;public string Resume;}
        static ScanArguments ParseScan(string[] args) {
            var values=new Dictionary<string,string>(StringComparer.Ordinal);
            var switches=new HashSet<string>(new[]{"--scan-archives","--sniff-renamed","--no-sniff-renamed","--full-hash","--include-appdata","--include-root-files","--exclude-root-files"},StringComparer.Ordinal);
            var names=new HashSet<string>(new[]{"--scan-root","--resume-scan","--workers","--scan-mode","--extensions","--max-age-years"},StringComparer.Ordinal);
            for(int i=0;i<args.Length;i++){
                string key=args[i];if(values.ContainsKey(key))throw new ArgumentException("Duplicate option "+key);
                if(switches.Contains(key)){values.Add(key,"");continue;}
                if(!names.Contains(key) || i+1>=args.Length || args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Invalid option "+key);
                values.Add(key,args[++i]);
            }
            string resume,root;
            bool resuming=values.TryGetValue("--resume-scan",out resume),scanning=values.TryGetValue("--scan-root",out root);
            if(resuming==scanning || (resuming && values.Keys.Any(k=>k!="--resume-scan" && k!="--workers")))throw new ArgumentException("Choose one start or resume action. A resumed scan retains its saved options.");
            if(values.ContainsKey("--sniff-renamed") && values.ContainsKey("--no-sniff-renamed") || values.ContainsKey("--include-root-files") && values.ContainsKey("--exclude-root-files"))throw new ArgumentException("Choose one setting for each scan option.");
            int workers=Number(values,"--workers",4,1,8),age=Number(values,"--max-age-years",0,0,ScanOptions.MaxModifiedAgeYears);
            string mode,extensions;
            if(!values.TryGetValue("--scan-mode",out mode))mode="quick";
            if(mode!="quick" && mode!="deep")throw new ArgumentException("Use quick or deep for --scan-mode.");
            return new ScanArguments {Resume=resume,Scan=new ScanOptions {RootPath=root,WorkerCount=workers,ModifiedAgeYears=age,
                ExcludeApplicationData=mode!="deep" && !values.ContainsKey("--include-appdata"),IncludeRootFiles=!values.ContainsKey("--exclude-root-files"),ScanArchives=values.ContainsKey("--scan-archives"),SniffRenamed=!values.ContainsKey("--no-sniff-renamed"),
                Extensions=values.TryGetValue("--extensions",out extensions) ? extensions.Split(',').Select(x=>x.Trim()).ToArray() : mode=="deep" ? Formats.Deep : Formats.Quick,
                FingerprintPolicy=values.ContainsKey("--full-hash") ? FingerprintPolicy.Full() : new FingerprintPolicy()}};
        }

        static int Number(Dictionary<string,string> values,string key,int fallback,int min,int max) {
            string value;if(!values.TryGetValue(key,out value))return fallback;
            int n;if(!Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out n) || n<min || n>max)throw new ArgumentException("Invalid value for "+key+". Use "+min+" through "+max+".");
            return n;
        }

        static void AwaitReady(ReviewerEngine engine) {
            while(!engine.Ready && !engine.Faulted && !engine.Closed)Thread.Sleep(100);
            if(!engine.Ready)throw new IOException(String.IsNullOrEmpty(engine.Status.Error) ? "The catalog did not open." : engine.Status.Error);
        }

        static void VerifyDependencies() {
            string path=Path.Combine(MediaScratch.EnsureDirectory(),"dependency-preview.png");
            try {
                File.WriteAllBytes(path,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/3Z8AAAAASUVORK5CYII="));
                if(Preview.Create(path).Width!=1)throw new InvalidDataException("The isolated decoder did not return the expected fixture.");
                using(var key=Crypto.DeriveKey(new byte[]{1,2,3},new byte[16],100000)) {
                    byte[] nonce=new byte[12],plain={12,34},aad={56};
                    if(!Crypto.Decrypt(key,nonce,aad,Crypto.Encrypt(key,nonce,aad,plain)).SequenceEqual(plain))throw new InvalidOperationException("Windows CNG authentication check failed.");
                }
                Program.ReadWindow();
                Console.WriteLine("Verified x64 startup, SQLite "+Native.SqliteVersion+", WPF, isolated image preview and Windows CNG.");
            }finally{if(File.Exists(path))File.Delete(path);}
        }
    }
}
