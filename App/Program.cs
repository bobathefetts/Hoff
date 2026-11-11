using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.2")]
[assembly:AssemblyTitle("Media Compliance Reviewer for Windows")]
[assembly:AssemblyCompany("IT1 Charlie McPherson")]

namespace MediaReviewer {
    static class Program {
        [DllImport("kernel32.dll")] static extern IntPtr GetCommandLineW();
        [DllImport("shell32.dll")] static extern IntPtr CommandLineToArgvW(IntPtr command,out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);

        static string[] Arguments() {
            int count;IntPtr args=CommandLineToArgvW(GetCommandLineW(),out count);
            if(args==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            try {return Enumerable.Range(1,count-1).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(args,i*IntPtr.Size))).ToArray();}
            finally{LocalFree(args);}
        }

        internal static Window ReadWindow() {
            var assembly=Assembly.GetExecutingAssembly();
            Window window;
            using(var markup=assembly.GetManifestResourceStream("MainWindow.xaml"))window=(Window)XamlReader.Load(markup);
            using(var icon=assembly.GetManifestResourceStream("Reviewer.ico")) {
                var frames=new IconBitmapDecoder(icon,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames;
                window.Icon=frames.FirstOrDefault(f=>f.PixelWidth==32)??frames[0];
            }
            return window;
        }

        static DateTime lastUnhandledReport;static string lastUnhandledMessage;static bool reportingUnhandled;
        // Keep the review session alive after an exception on the UI thread. Catalog state lives in SQLite
        // transactions, so continuing is safe, while a crash would lose the reviewer's place and queue.
        // The window smoke test stays unhandled so a regression fails the run instead of being logged, and so
        // does an exception during the one-way shutdown sequence, where swallowing it would leave a window
        // that can never close.
        static void AttachUnhandledExceptionHandlers(Application app,bool interactive,Func<bool> shuttingDown) {
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>Console.Error.WriteLine("Unhandled exception: "+e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException+=(s,e)=>{Console.Error.WriteLine("Unobserved task exception: "+e.Exception);e.SetObserved();};
            app.DispatcherUnhandledException+=(s,e)=>{
                Console.Error.WriteLine("Dispatcher exception: "+e.Exception);
                if(!interactive || shuttingDown())return;
                e.Handled=true;
                string message=e.Exception.GetBaseException().Message;
                // One dialog at a time, and one per distinct message per ten seconds, so a repeating timer fault
                // cannot stack dialogs while the first one is still open.
                if(reportingUnhandled || (message==lastUnhandledMessage && DateTime.UtcNow-lastUnhandledReport<TimeSpan.FromSeconds(10)))return;
                lastUnhandledMessage=message;lastUnhandledReport=DateTime.UtcNow;reportingUnhandled=true;
                try{MessageBox.Show(message,"Media Compliance Reviewer · unexpected error",MessageBoxButton.OK,MessageBoxImage.Error);}catch(Exception){}finally{reportingUnhandled=false;}
            };
        }

        [STAThread] static int Main() {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
            return Run();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Run() {
            string[] args=Arguments();
            string mode=args.FirstOrDefault();
            bool headless=CommandLine.IsHeadless(args);
            string scratch=Path.Combine(Path.GetTempPath(),"MediaComplianceReviewer-"+Guid.NewGuid().ToString("N"));
            try {
                if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("The desktop entry point needs an STA thread.");
                Native.Initialize(scratch);
                if(mode=="--preview-worker")return DecoderProgram.Run();
                if(mode=="--draft-worker" && (args.Length==1 || args.Length==2 && (args[1]=="gemma" || args[1]=="gemma-vulkan")))return DraftModelWorker.Run(args.Length==2,args.Length==2 && args[1]=="gemma-vulkan");
                if(mode=="--playback-worker")return PlaybackProxy.Worker();
                MediaScratch.QueueRecovery();
                if(headless)return CommandLine.Run(args);
                if(mode=="--self-test" && (args.Length==2 || args.Length==3))return Smoke.Run(args[1],args.Length==3 ? args[2] : null);
                if(mode=="--verify-previews" && args.Length>=2)return Smoke.PreviewFiles(args.Skip(1));
                if(mode=="--verify-import" && args.Length==3)return Smoke.Import(args[1],args[2]);
                if(mode!=null && mode!="--window-smoke" && (mode!="--first-scan-smoke" || args.Length!=2) && ((mode!="--database" && mode!="--open-evidence-package") || args.Length!=2))throw new ArgumentException("Use --database with a local SQLite path, --open-evidence-package with a package path, or launch without arguments.");
                var app=new Application();
                bool interactive=mode!="--window-smoke" && mode!="--first-scan-smoke";
                // The window tests must never create or open the real default catalog in the user profile.
                if(!interactive)ReviewerWindow.DefaultCatalogOverride=Path.Combine(mode=="--first-scan-smoke" ? args[1] : Path.GetDirectoryName(args[1]),"default-catalog","Media Review Catalog.sqlite");
                using(var controller=new ReviewerWindow(ReadWindow(),interactive)) {
                    AttachUnhandledExceptionHandlers(app,interactive,()=>controller.IsClosing);
                    // No startup prompt: reopen the catalog an earlier scan created, or wait for the first scan to create it.
                    if(mode==null)controller.Window.Loaded+=(s,e)=>controller.Window.Dispatcher.BeginInvoke(new Action(controller.OpenDefaultCatalogIfPresent));
                    if(mode=="--first-scan-smoke") {
                        bool passed=false;
                        controller.Window.Width=1024;controller.Window.Height=768;
                        controller.Window.Loaded+=(s,e)=>controller.Window.Dispatcher.BeginInvoke(new Action(()=>{
                            try{Smoke.CheckFirstScanCreatesCatalog(controller,args[1]);passed=true;}
                            catch(Exception error){Console.Error.WriteLine(error);}
                            controller.Window.Close();
                        }));
                        app.Run(controller.Window);
                        return passed ? 0 : 1;
                    }
                    if(mode=="--database")controller.Open(args[1],false);
                    if(mode=="--open-evidence-package")controller.Window.Loaded+=(s,e)=>controller.Window.Dispatcher.BeginInvoke(new Action(()=>{
                        try{controller.OpenPackage(args[1]);}catch(Exception error){MessageBox.Show(controller.Window,error.Message,"Could not open Evidence package");}
                        finally{controller.Window.Close();}
                    }));
                    if(mode=="--window-smoke") {
                        if(args.Length!=2)throw new ArgumentException("Window smoke test needs a fixture database.");
                        controller.Window.Width=1024;controller.Window.Height=768;
                        controller.Window.Loaded+=(s,e)=>controller.Window.Dispatcher.BeginInvoke(new Action(()=>{
                            try{Smoke.CheckNoStartupPrompt(controller.Window);Smoke.CheckCatalogPath(args[1]);controller.Open(args[1],false);}
                            catch(Exception error){Console.Error.WriteLine(error);controller.Window.Close();}
                        }));
                        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
                        var elapsed=System.Diagnostics.Stopwatch.StartNew();
                        bool rendered=false;
                        int reviewsStarted=0;
                        timer.Tick+=(s,e)=>{
                            if(reviewsStarted!=0) {
                                var message=((System.Windows.Controls.TextBlock)controller.Window.FindName("MessageText")).Text;
                                if(message!="Review saved." && elapsed.Elapsed<TimeSpan.FromSeconds(12))return;
                                timer.Stop();rendered=message=="Review saved.";
                                if(!rendered)Console.Error.WriteLine("Quick Review did not commit: "+message);
                                if(rendered && reviewsStarted==1) {
                                    try{
                                        Smoke.MarkQuickNonCompliant(controller.Window);
                                        reviewsStarted++;elapsed.Restart();timer.Start();return;
                                    }catch(Exception error){rendered=false;Console.Error.WriteLine(error);}
                                }
                                controller.Window.Close();return;
                            }
                            if(!controller.HasRenderedPreview && elapsed.Elapsed<TimeSpan.FromSeconds(12))return;
                            rendered=controller.HasRenderedPreview;timer.Stop();
                            try{if(rendered){
                                string output=Path.GetDirectoryName(args[1]);
                                Smoke.CheckShell(controller.Window,Path.Combine(output,"gallery.png"));
                                controller.Window.Width=1280;controller.Window.UpdateLayout();
                                if(controller.Window.ActualWidth>=1160) {
                                    Smoke.CheckWideShell(controller.Window);
                                    Smoke.SaveGallery(controller.Window,Path.Combine(output,"workspace-dark.png"));
                                }
                                Smoke.CheckQuickNavigationAndMarkCompliant(controller.Window,controller.ApplyPage);
                                reviewsStarted++;elapsed.Restart();timer.Start();return;
                            }}
                            catch(Exception error){rendered=false;Console.Error.WriteLine(error);}
                            controller.Window.Close();
                        };
                        timer.Start();app.Run(controller.Window);
                        if(rendered)Smoke.CheckSavedQuickReviews(args[1]);
                        return rendered ? 0 : 1;
                    }
                    return app.Run(controller.Window);
                }
            }catch(Exception error) {
                if(headless || mode=="--self-test" || mode=="--window-smoke" || mode=="--first-scan-smoke" || mode=="--preview-worker" || mode=="--draft-worker" || mode=="--playback-worker" || mode=="--verify-previews" || mode=="--verify-import")Console.Error.WriteLine(error);
                else MessageBox.Show(error.GetBaseException().Message,"Media Compliance Reviewer could not start",MessageBoxButton.OK,MessageBoxImage.Error);
                return 1;
            }finally {
                if(mode!="--preview-worker" && mode!="--draft-worker") {
                    Preview.Shutdown();
                    MediaScratch.Shutdown();
                }
            }
        }
    }
}
