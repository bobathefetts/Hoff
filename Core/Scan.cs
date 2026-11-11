using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    public sealed class ScanOptions {
        public const int MaxModifiedAgeYears=200;
        public string RootPath; public int WorkerCount=4,ModifiedAgeYears;
        public string ManifestRootIdentity,ManifestSourceHead;
        public bool ExcludeApplicationData=true,IncludeRootFiles=true,ScanArchives,SniffRenamed=true;
        public string[] Extensions=Formats.Quick; public long CutoffUtcTicks;
        public FingerprintPolicy FingerprintPolicy=new FingerprintPolicy();
        public static ScanOptions FromJson(string json){
            var serializer=new JavaScriptSerializer();var saved=serializer.DeserializeObject(json) as Dictionary<string,object>;
            var options=serializer.Deserialize<ScanOptions>(json);
            if(saved==null || !saved.ContainsKey("FingerprintPolicy"))options.FingerprintPolicy=MediaReviewer.FingerprintPolicy.Full();
            if(options.FingerprintPolicy==null)throw new InvalidDataException("Saved scan has no fingerprint policy");
            options.FingerprintPolicy.Validate();return options;
        }
        internal ScanOptions Copy() { var copy=(ScanOptions)MemberwiseClone(); copy.Extensions=(string[])Extensions.Clone();copy.FingerprintPolicy=FingerprintPolicy.Copy();return copy; }
        internal void Validate() {
            if(WorkerCount<1 || WorkerCount>8 || ModifiedAgeYears<0 || ModifiedAgeYears>MaxModifiedAgeYears || Extensions==null || Extensions.Length==0) throw new ArgumentException("Choose 1–8 workers, an age of 0–200 years, and selected media types.");
            Extensions=Extensions.Select(x=>x.TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
            if(Extensions.Any(x=>!Formats.Supported(x))) throw new ArgumentException("An unknown media extension was selected. Use the available image, RAW and video families.");
            if(FingerprintPolicy==null)throw new ArgumentException("A fingerprint policy is required");FingerprintPolicy.Validate();
            RootPath=Paths.Source(RootPath);
            if(ModifiedAgeYears==0) CutoffUtcTicks=0;
            else if(CutoffUtcTicks==0) CutoffUtcTicks=DateTime.UtcNow.AddYears(-ModifiedAgeYears).Ticks;
        }
    }
    internal sealed class ScanJob { internal long Id; internal int User; internal string Path,Kind,Signature,Occurrence;internal bool RequiresFull; }
    internal sealed class DiscoveryRow { internal string Path,Kind,State,Signature,Error; internal long Cost { get { return Path.Length*2+2048; } } }
    internal sealed partial class ScanRunner {
        readonly ReviewerEngine engine; readonly string id; readonly bool resume; readonly int resumeWorkers;
        readonly Thread coordinator; readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        readonly BoundedQueue<FileRecord> prepared=new BoundedQueue<FileRecord>(32,4*1024*1024);
        readonly List<Thread> fileWorkers=new List<Thread>(); Thread publisher,decoder;
        readonly object reviewMatchGate=new object();
        ScanOptions options; string rootId; int currentUser=-1,activeHash; volatile bool finished; volatile string workerFailure;
        // A link is recorded with its own state so it is never counted as discovered or processed media.
        // The first few are named in the diagnostics log, and the scan summary gives the total.
        internal const string NotFollowed="not_followed"; const int ShortcutLogLimit=20;
        const string ShortcutMessage="Not followed: this is a link (junction, symbolic link or mount point) that can lead to another location. Scan its target folder directly if it should be reviewed.";
        int shortcutsLogged;
        internal bool Finished { get { return finished; } }
        internal ScanRunner(ReviewerEngine owner,ScanOptions config,string scanId,bool continuing,int workers) {
            engine=owner; options=config; id=scanId; resume=continuing; resumeWorkers=workers;
            coordinator=new Thread(Run) { IsBackground=true,Name="MediaReviewer discovery" };
        }
        internal void Start() { coordinator.Start(); }
        internal void Cancel() { cancellation.Cancel(); engine.Update(s=>{s.CancelRequested=true;s.Stage="Cancellation requested; waiting for owned workers";}); }
        internal void Join() { if(Thread.CurrentThread!=coordinator) coordinator.Join(); }
        object Write(Func<SqliteConnection,object> body,long bytes=1024) { return engine.WaitWrite(body,bytes); }
        string Full(string relative) { return String.IsNullOrEmpty(relative) ? options.RootPath : Catalog.PreviewSourcePath(options.RootPath,relative); }
        void Run() {
            bool completed=false,withErrors=false,manifestStarted=false; string failure=null;
            try {
                if(resume) {
                    object[] row=(object[])Write(db=> {
                        var rows=db.Query(@"SELECT s.config_json,s.root_id,s.manifest_complete,s.state,r.path,
 COALESCE(h.connection_id,''),CASE WHEN c.applied_at>s.started_at THEN 1 ELSE 0 END
 FROM scans s JOIN source_roots r ON r.id=s.root_id
 LEFT JOIN root_source_heads h ON h.root_id=s.root_id LEFT JOIN source_connections c ON c.id=h.connection_id
 WHERE s.id=?",new object[]{id},1);
                        if(rows.Count==0)throw new InvalidDataException("Saved scan does not exist");
                        return rows[0];
                    });
                    if((string)row[3]=="complete" || (string)row[3]=="complete_errors") throw new InvalidOperationException("This scan is already complete. Start a fresh scan for additions.");
                    options=ScanOptions.FromJson((string)row[0]);options.WorkerCount=resumeWorkers;rootId=(string)row[1];
                    string currentPath=(string)row[4];
                    string currentIdentity=Paths.ScanRootIdentity(currentPath);
                    bool samePath=!String.IsNullOrEmpty(options.RootPath) && Paths.Key(Paths.Source(options.RootPath))==Paths.Key(currentPath);
                    bool sameBinding=options.ManifestSourceHead==null ? (long)row[6]==0 : options.ManifestSourceHead==(string)row[5];
                    bool sameRoot=options.ManifestRootIdentity!=null ? options.ManifestRootIdentity==currentIdentity : samePath && sameBinding;
                    if(!sameRoot)throw new InvalidOperationException("The saved manifest's source folder cannot be verified at its current location. Restore the original source or start a fresh scan. Saved reviews and checkpoints are preserved.");
                    options.RootPath=currentPath;options.Validate();
                    Write(db=> { db.Execute("UPDATE jobs SET state='pending' WHERE scan_id=? AND state='processing'",id); db.Execute("UPDATE scans SET state='running' WHERE id=?",id); return null; });
                    manifestStarted=true;
                    if((long)row[2]==0) Manifest();
                } else {
                    options.Validate();
                    options.ManifestRootIdentity=Paths.ScanRootIdentity(options.RootPath);
                    rootId=(string)Write(db=>{
                        string root=Catalog.Root(db,options.RootPath);
                        options.ManifestSourceHead=(string)db.Scalar("SELECT connection_id FROM root_source_heads WHERE root_id=?",root)??"";
                        string json=new JavaScriptSerializer().Serialize(options);
                        db.Execute("INSERT INTO scans(id,root_id,state,started_at,config_json) VALUES(?,?,'manifest',?,?)",id,root,Catalog.Now(),json);
                        return root;
                    });
                    manifestStarted=true;
                    Manifest();
                }
                int total=Convert.ToInt32(Write(db=>db.Scalar("SELECT COUNT(*) FROM scan_users WHERE scan_id=?",id)));
                int done=Convert.ToInt32(Write(db=>db.Scalar("SELECT COUNT(*) FROM scan_users WHERE scan_id=? AND state IN('complete','complete_errors','skipped')",id)));
                engine.Update(s=>{s.TotalUsers=total;s.CompletedUsers=done;s.ResumeId=id;});
                StartWorkers();
                while(true) {
                    cancellation.Token.ThrowIfCancellationRequested();
                    object[] user=(object[])Write(db=> { var users=db.Query("SELECT ordinal,name,path,root_files FROM scan_users WHERE scan_id=? AND state NOT IN('complete','complete_errors','skipped') ORDER BY ordinal LIMIT 1",new object[]{id},1); return users.Count==0 ? null : users[0]; });
                    if(user==null) break;
                    int ordinal=Convert.ToInt32(user[0]); Volatile.Write(ref currentUser,ordinal);
                    engine.Update(s=>{s.User=(string)user[1];s.Stage="Discovering and fingerprinting "+s.User;});
                    Write(db=> { db.Execute("UPDATE scan_users SET state='running' WHERE scan_id=? AND ordinal=?",id,ordinal); db.Execute("UPDATE scans SET current_user=? WHERE id=?",ordinal,id); return null; });
                    while(true) {
                        cancellation.Token.ThrowIfCancellationRequested(); ScanJob directory=Claim("directory",ordinal);
                        if(directory!=null) { Discover(directory,Convert.ToInt64(user[3])!=0); continue; }
                        ScanJob archive=Claim("archive",ordinal);if(archive!=null){DiscoverArchive(archive);continue;}
                        long pending=Convert.ToInt64(Write(db=>db.Scalar("SELECT COUNT(*) FROM jobs WHERE scan_id=? AND user_ordinal=? AND state IN('pending','processing')",id,ordinal)));
                        Refresh(); if(pending==0) break;
                        if(cancellation.Token.WaitHandle.WaitOne(50)) cancellation.Token.ThrowIfCancellationRequested();
                    }
                    bool errors=Convert.ToInt64(Write(db=>db.Scalar("SELECT COUNT(*) FROM jobs WHERE scan_id=? AND user_ordinal=? AND state='failed'",id,ordinal)))>0;
                    Write(db=> { db.Execute("UPDATE scan_users SET state=? WHERE scan_id=? AND ordinal=?",errors ? "complete_errors" : "complete",id,ordinal); return null; });
                    withErrors|=errors; done++; engine.Update(s=>s.CompletedUsers=done);
                }
                withErrors=Convert.ToInt64(Write(db=>db.Scalar("SELECT COUNT(*) FROM jobs WHERE scan_id=? AND state='failed'",id)))>0;
                completed=true;
            } catch(OperationCanceledException) { if(workerFailure!=null) failure="Worker failure: "+workerFailure; } catch(Exception error) { failure=FileAccessNative.Describe(error); }
            finally {
                cancellation.Cancel(); prepared.Close();
                foreach(Thread worker in fileWorkers) worker.Join(); if(publisher!=null) publisher.Join(); if(decoder!=null) decoder.Join();
                if(!engine.Faulted) {
                    try {
                        Write(db=> {
                            Verification.Transaction(db,()=>{
                                if(manifestStarted){
                                    db.Execute("UPDATE jobs SET state='pending' WHERE scan_id=? AND state='processing'",id);
                                    db.Execute("UPDATE scans SET state=?,completed_at=? WHERE id=?",completed ? (withErrors ? "complete_errors" : "complete") : failure==null ? "cancelled" : "failed",completed ? Catalog.Now() : null,id);
                                }
                                long shortcuts=Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(total),0) FROM scan_counters WHERE scan_id=? AND state=?",id,NotFollowed))+
                                    Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM scan_users WHERE scan_id=? AND state='skipped'",id));
                                Catalog.Log(db,failure==null ? "info" : "error","Scan summary",options==null ? "" : options.RootPath,
                                    (completed ? "Scan completed"+(withErrors ? " with errors" : " cleanly") : failure ?? "Scan cancelled; committed results and pending jobs are retained")+ShortcutSummary(shortcuts));
                            });
                            return null;
                        }); Refresh();
                    } catch(Exception error) { if(failure==null) failure=error.Message; }
                }
                finished=true;
                engine.Update(s=>{s.RunningScan=false;s.CompletedWithErrors=withErrors;if(!engine.Faulted)s.Error=failure??s.Error;s.ResumeId=completed ? "" : id;s.PreparedQueueCount=0;s.RetainedBytes=0;s.Stage=(engine.Faulted ? "Database stopped; scan interrupted" : completed ? (withErrors ? "Completed with errors" : "Completed") : failure==null ? "Cancelled; resume is available" : "Scan stopped: "+failure)+(engine.Faulted ? "" : ShortcutSummary(s.NotFollowed));});
            }
        }
        internal static string LinksNotFollowed(long count) {
            return count.ToString("N0",CultureInfo.CurrentCulture)+(count==1 ? " link" : " links")+" not followed";
        }
        static string ShortcutSummary(long count) { return count<=0 ? "" : " · "+LinksNotFollowed(count)+" (see the diagnostics log)"; }
        void LogShortcut(SqliteConnection db,string kind,string relative) {
            if(shortcutsLogged>=ShortcutLogLimit) return; shortcutsLogged++;
            Catalog.Log(db,"warning",kind,Full(relative),ShortcutMessage);
        }
        void Manifest() {
            var rows=new List<SourceEntry>();
            if(options.IncludeRootFiles) Write(db=> { db.Execute("INSERT OR IGNORE INTO scan_users(scan_id,name,path,path_key,sort_key,state,root_files) VALUES(?,'(Root files)','','','','pending',1)",id); return null; });
            Retry(()=> {
                try {
                    foreach(SourceEntry entry in FileAccessNative.Entries(options.RootPath,cancellation.Token)) {
                        if(!entry.Directory || entry.CompatibilityLink) continue; rows.Add(entry); if(rows.Count>=32) FlushManifest(rows);
                    }
                } finally { if(rows.Count>0 && !cancellation.IsCancellationRequested) FlushManifest(rows); }
            },"Listing immediate user folders",options.RootPath,null);
            int next=Convert.ToInt32(Write(db=>db.Scalar("SELECT COALESCE(MAX(ordinal),-1)+1 FROM scan_users WHERE scan_id=?",id)));
            while(true) {
                cancellation.Token.ThrowIfCancellationRequested();
                var pending=(List<object[]>)Write(db=>db.Query("SELECT name,path,state,root_files FROM scan_users WHERE scan_id=? AND ordinal IS NULL ORDER BY root_files DESC,sort_key,path LIMIT 32",new object[]{id},32));
                if(pending.Count==0) break; int start=next; next+=pending.Count;
                Write(db=> {
                    db.Execute("BEGIN IMMEDIATE"); try { int ordinal=start; foreach(object[] row in pending) { db.Execute("UPDATE scan_users SET ordinal=? WHERE scan_id=? AND path_key=?",ordinal,id,Paths.Key((string)row[1])); if((string)row[2]!="skipped") db.Execute("INSERT OR IGNORE INTO jobs(scan_id,user_ordinal,kind,path,path_key,state) VALUES(?,?,'directory',?,?,'pending')",id,ordinal,row[1],Paths.Key((string)row[1])); ordinal++; } db.Execute("COMMIT"); } catch { Catalog.Rollback(db); throw; } return null;
                },pending.Sum(x=>((string)x[1]).Length*2+128));
            }
            Write(db=> { db.Execute("UPDATE scans SET manifest_complete=1,state='running' WHERE id=?",id); return null; });
        }
        void FlushManifest(List<SourceEntry> rows) {
            var batch=new List<SourceEntry>(rows); rows.Clear();
            int added=(int)Write(db=> {
                db.Execute("BEGIN IMMEDIATE"); try { int changes=0; foreach(SourceEntry entry in batch) { int inserted=db.Execute("INSERT OR IGNORE INTO scan_users(scan_id,name,path,path_key,sort_key,state) VALUES(?,?,?,?,?,?)",id,entry.Name,entry.Name,Paths.Key(entry.Name),entry.Name.ToUpperInvariant(),entry.Shortcut ? "skipped" : "pending"); if(inserted>0 && entry.Shortcut) LogShortcut(db,"directory",entry.Name); changes+=inserted; } db.Execute("COMMIT"); return changes; } catch { Catalog.Rollback(db); throw; }
            },batch.Sum(x=>x.Name.Length*4+128));
            engine.Update(s=>{s.TotalUsers+=added;s.Stage="Saving user-folder manifest: "+s.TotalUsers;s.LastProgressUtc=DateTime.UtcNow;});
        }
        ScanJob Claim(string kind,int ordinal) {
            return (ScanJob)Write(db=> {
                db.Execute("BEGIN IMMEDIATE"); try { var rows=db.Query("SELECT id,path,signature,occurrence_id FROM jobs WHERE scan_id=? AND user_ordinal=? AND kind=? AND state='pending' ORDER BY id LIMIT 1",new object[]{id,ordinal,kind},1);
                    if(rows.Count==0) { db.Execute("COMMIT"); return null; }
                    var row=rows[0];
                    bool full=kind=="file" && db.Scalar("SELECT 1 FROM group_occurrences o JOIN contents c ON c.id=o.content_id WHERE o.root_id=? AND o.path_key=? AND o.superseded=0 AND c.algorithm='sha256' LIMIT 1",rootId,Paths.Key((string)row[1]))!=null;
                    db.Execute("UPDATE jobs SET state='processing' WHERE id=?",row[0]);db.Execute("COMMIT");return new ScanJob {Id=(long)row[0],Path=(string)row[1],Kind=kind,User=ordinal,Signature=(string)row[2],Occurrence=(string)row[3],RequiresFull=full};
                } catch { Catalog.Rollback(db); throw; }
            });
        }
        void Retry(Action action,string stage,string path,ScanJob job) {
            for(int attempt=0;;attempt++) {
                cancellation.Token.ThrowIfCancellationRequested();
                if(job!=null) Write(db=> { db.Execute("UPDATE jobs SET attempts=attempts+1 WHERE id=?",job.Id); return null; });
                try { action(); return; }
                catch(Exception error) {
                    if(error is OperationCanceledException || cancellation.IsCancellationRequested) throw new OperationCanceledException();
                    if(attempt>=3 || !FileAccessNative.Transient(error)) throw;
                    int retry=attempt+1; Write(db=> { Catalog.Log(db,"warning",stage,path,"Retry "+retry+" of 3: "+FileAccessNative.Describe(error)); return null; });
                    engine.Update(s=>s.Stage=stage+" · retry "+retry+" of 3");
                    if(cancellation.Token.WaitHandle.WaitOne(retry==1 ? 250 : retry==2 ? 1000 : 3000)) throw new OperationCanceledException();
                }
            }
        }
        void Discover(ScanJob job,bool rootFiles) {
            var batch=new List<DiscoveryRow>(); long retained=0;
            try {
                Retry(()=> {
                    try {
                        foreach(SourceEntry entry in FileAccessNative.Entries(Full(job.Path),cancellation.Token)) {
                            if((rootFiles && entry.Directory) || entry.CompatibilityLink) continue;
                            if(entry.Directory && options.ExcludeApplicationData && (entry.Name.Equals("AppData",StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("Application Data",StringComparison.OrdinalIgnoreCase))) continue;
                            string relative=String.IsNullOrEmpty(job.Path) ? entry.Name : Path.Combine(job.Path,entry.Name);
                            string kind=entry.Directory ? "directory" : "file";
                            if(!entry.Directory) {
                                string ext=Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
                                if(options.ScanArchives && ext=="zip")kind="archive";
                                // A file stored elsewhere (cloud placeholder, tiered or archived) is fetched when read, so
                                // its first bytes are checked for renamed media only when it has a selected media extension.
                                else if((options.CutoffUtcTicks>0 && entry.ModifiedTicks<options.CutoffUtcTicks) ||
                                    !options.Extensions.Contains(ext) && (!options.SniffRenamed || entry.StoredElsewhere || Formats.Supported(ext) || ext=="zip"))continue;
                            }
                            var row=new DiscoveryRow { Path=relative,Kind=kind,State=entry.Shortcut ? NotFollowed : "pending" }; batch.Add(row); retained+=row.Cost;
                            if(batch.Count>=32 || retained>=1024*1024) { FlushDiscovery(batch,job.User); retained=0; Backpressure(job.User); }
                        }
                    } finally { if(batch.Count>0 && !cancellation.IsCancellationRequested) { FlushDiscovery(batch,job.User); retained=0; } }
                },"Listing folder",Full(job.Path),job);
                Finish(job,"complete",null);
            } catch(OperationCanceledException) { throw; } catch(Exception error) { Finish(job,"failed",error); }
        }
        void FlushDiscovery(List<DiscoveryRow> rows,int ordinal) {
            var batch=new List<DiscoveryRow>(rows); rows.Clear();
            Write(db=> { db.Execute("BEGIN IMMEDIATE"); try { foreach(DiscoveryRow row in batch) { int added=db.Execute("INSERT OR IGNORE INTO jobs(scan_id,user_ordinal,kind,path,path_key,state,signature,error) VALUES(?,?,?,?,?,?,?,?)",id,ordinal,row.Kind,row.Path,Paths.Key(row.Path),row.State,row.Signature,row.Error); if(added>0 && row.State==NotFollowed) LogShortcut(db,row.Kind,row.Path); } db.Execute("COMMIT"); } catch { Catalog.Rollback(db); throw; } return null; },batch.Sum(x=>x.Cost));
            engine.Update(s=>s.LastProgressUtc=DateTime.UtcNow);
        }
        void Backpressure(int ordinal) {
            while(Convert.ToInt64(Write(db=>db.Scalar("SELECT COUNT(*) FROM (SELECT id FROM jobs WHERE scan_id=? AND user_ordinal=? AND kind='file' AND state IN('pending','processing') LIMIT 161)",id,ordinal)))>128) {
                Refresh(); if(cancellation.Token.WaitHandle.WaitOne(50)) throw new OperationCanceledException();
            }
        }
        void DiscoverArchive(ScanJob job){
            engine.Update(s=>s.Stage="Validating ZIP directory: "+job.Path);
            // A refused member is recorded as a failed file here instead of being queued for work that can only fail.
            // Every one counts as a failed file, but only the first few are written to the 500-row log, so a single
            // protected ZIP cannot push the scan's other errors out of it.
            var refusedPaths=new List<string>();var refusedReasons=new List<string>();long refused=0;
            try{Retry(()=>{refusedPaths.Clear();refusedReasons.Clear();refused=0;using(var directory=new Archives.Directory(Full(job.Path),options.RootPath,cancellation.Token)){
                engine.Update(s=>s.Stage="Discovering ZIP members: "+job.Path);
                Write(db=>{var stamp=directory.Stamp;var saved=db.Query("SELECT archive_size,archive_ticks,archive_identity FROM archive_manifests WHERE scan_id=? AND path_key=?",new object[]{id,Paths.Key(job.Path)},1);if(saved.Count>0 && ((long)saved[0][0]!=stamp.Size || (long)saved[0][1]!=stamp.ModifiedTicks || (string)saved[0][2]!=stamp.Identity))throw new InvalidDataException("ZIP archive changed after saved discovery; start a fresh scan");db.Execute("INSERT OR IGNORE INTO archive_manifests VALUES(?,?,?,?,?,?)",id,Paths.Key(job.Path),job.Path,stamp.Size,stamp.ModifiedTicks,stamp.Identity);return null;});
                var batch=new List<DiscoveryRow>();long cost=0;foreach(var member in directory.Members()){
                    cancellation.Token.ThrowIfCancellationRequested();if(member.IsDirectory)continue;string ext=Path.GetExtension(member.Name).TrimStart('.').ToLowerInvariant();
                    // Like loose files, an unknown extension is listed so its first bytes can be checked for renamed media.
                    // A nested archive is listed too, so it is reported as unscanned content instead of being hidden.
                    bool wanted=options.Extensions.Contains(ext) || Archives.Nested(member.Name) || (options.SniffRenamed && !Formats.Supported(ext));
                    if(!wanted || (options.CutoffUtcTicks>0 && member.ModifiedTicks<options.CutoffUtcTicks) || (options.ExcludeApplicationData && member.Name.Split('\\').Any(x=>x.Equals("AppData",StringComparison.OrdinalIgnoreCase) || x.Equals("Application Data",StringComparison.OrdinalIgnoreCase))))continue;
                    var row=new DiscoveryRow {Path=job.Path+Archives.Separator+member.Name,Kind="file",State=member.Refusal==null ? "pending" : "failed",Signature=member.Identity,Error=member.Refusal};
                    if(member.Refusal!=null){refused++;if(refusedPaths.Count<5){refusedPaths.Add(row.Path);refusedReasons.Add(member.Refusal);}}
                    batch.Add(row);cost+=row.Cost;if(batch.Count>=32 || cost>=1024*1024){FlushDiscovery(batch,job.User);cost=0;Backpressure(job.User);}
                }if(batch.Count>0)FlushDiscovery(batch,job.User);directory.Verify();
            }},"Discovering ZIP",Full(job.Path),job);
            if(refused>0)Write(db=>{
                for(int i=0;i<refusedPaths.Count;i++)Catalog.Log(db,"error","file",Full(refusedPaths[i]),refusedReasons[i]);
                if(refused>refusedPaths.Count)Catalog.Log(db,"error","archive",Full(job.Path),(refused-refusedPaths.Count)+" more members of this ZIP were refused and not examined. Each is counted as a failed file.");
                return null;
            });
            Finish(job,"complete",null);engine.Update(s=>s.Stage="Fingerprinting and previewing "+job.Path);}catch(OperationCanceledException){throw;}catch(Exception error){Finish(job,"failed",error);}
        }
        void Finish(ScanJob job,string state,Exception error) {
            string description=error==null ? null : FileAccessNative.Describe(error);
            Write(db=> { db.Execute("BEGIN IMMEDIATE"); try { db.Execute("UPDATE jobs SET state=?,error=? WHERE id=?",state,description,job.Id); if(error!=null) Catalog.Log(db,"error",job.Kind,Full(job.Path),description); db.Execute("COMMIT"); } catch { Catalog.Rollback(db); throw; } return null; });
        }
        void StartWorkers() {
            for(int i=0;i<options.WorkerCount;i++) { var thread=new Thread(Files) { IsBackground=true,Name="MediaReviewer file worker "+i }; fileWorkers.Add(thread); thread.Start(); }
            publisher=new Thread(Publish) { IsBackground=true,Name="MediaReviewer fingerprint publisher" }; publisher.Start();
            decoder=new Thread(Previews) { IsBackground=true,Name="MediaReviewer preview worker" }; decoder.Start();
        }
        void Files() {
            byte[] buffer=new byte[1024*1024];
            try {
                while(!cancellation.IsCancellationRequested) {
                    int ordinal=Volatile.Read(ref currentUser); if(ordinal<0) { cancellation.Token.WaitHandle.WaitOne(25); continue; }
                    ScanJob job=Claim("file",ordinal); if(job==null) { cancellation.Token.WaitHandle.WaitOne(25); continue; }
                    Interlocked.Increment(ref activeHash);
                    try {
                        FileRecord record=null;
                        Retry(()=>{
                            bool archive=Archives.IsPath(job.Path);
                            string name=Path.GetFileName(archive ? Archives.Split(job.Path)[1] : job.Path);
                            var item=new FileRecord {RootId=rootId,ScanId=id,JobId=job.Id,UserOrdinal=job.User,RelativePath=job.Path,Name=name,Extension=Path.GetExtension(name).TrimStart('.').ToLowerInvariant()};
                            Action<int> progress=bytes=>engine.Update(s=>{s.BytesRead+=bytes;s.LastProgressUtc=DateTime.UtcNow;});
                            var sniff=options.SniffRenamed && !Formats.Supported(item.Extension) ? options.Extensions : null;
                            record=archive ? Archives.Fingerprint(Full(job.Path),item,buffer,cancellation.Token,progress,options.CutoffUtcTicks,options.RootPath,job.Signature,sniff)
                                : FileAccessNative.FingerprintScoped(Full(job.Path),item,buffer,cancellation.Token,progress,options.CutoffUtcTicks,job.RequiresFull ? MediaReviewer.FingerprintPolicy.Full() : options.FingerprintPolicy,options.RootPath,sniff);
                            if(record!=null)record=MatchCatalogIdentity(record,buffer);
                        },"Fingerprinting",Full(job.Path),job);
                        if(record==null) Finish(job,"skipped",null); else {
                            prepared.Add(record,record.RelativePath.Length*2+512,cancellation.Token);
                        }
                    } catch(OperationCanceledException) { throw; } catch(Exception error) { Finish(job,"failed",error); }
                    finally { Interlocked.Decrement(ref activeHash); }
                }
            } catch(OperationCanceledException) {} catch(Exception error) { FailWorker(error); }
        }
        void Publish() {
            var batch=new List<FileRecord>(); long bytes=0; DateTime first=DateTime.UtcNow;
            try {
                while(!cancellation.IsCancellationRequested) {
                    FileRecord item=prepared.Take(cancellation.Token,25);
                    if(item!=null) { if(batch.Count==0) first=DateTime.UtcNow; batch.Add(item); bytes+=item.RelativePath.Length*2+512; }
                    if(batch.Count>0 && (batch.Count>=32 || bytes>=1024*1024 || DateTime.UtcNow-first>TimeSpan.FromMilliseconds(250) || (prepared.Count==0 && Volatile.Read(ref activeHash)==0))) {
                        var current=new List<FileRecord>(batch);lock(reviewMatchGate)Write(db=> { Catalog.PublishBatch(db,current); return null; },bytes); batch.Clear(); bytes=0;
                    }
                }
            } catch(OperationCanceledException) {} catch(Exception error) { FailWorker(error); }
        }
        void Previews() {
            try {
                while(!cancellation.IsCancellationRequested) {
                    int ordinal=Volatile.Read(ref currentUser); if(ordinal<0) { cancellation.Token.WaitHandle.WaitOne(25); continue; }
                    ScanJob job=Claim("preview",ordinal); if(job==null) { cancellation.Token.WaitHandle.WaitOne(25); continue; }
                    long content=Int64.Parse(job.Signature,CultureInfo.InvariantCulture);
                    try {
                        if(Convert.ToInt64(Write(db=>db.Scalar("SELECT COUNT(*) FROM previews WHERE content_id=? AND state='available'",content)))>0) { Finish(job,"complete",null); continue; }
                        object[] meta=(object[])Write(db=>db.Query("SELECT size,modified_ticks,file_identity FROM bound_occurrences WHERE id=?",new object[]{job.Occurrence},1)[0]);
                        PreviewResult preview=null;
                        Retry(()=>preview=Preview.CreateExpectedScoped(Full(job.Path),(long)meta[0],(long)meta[1],(string)meta[2],256,32768,cancellation.Token,options.RootPath),"Creating preview",Full(job.Path),job);
                        Write(db=> { db.Execute("BEGIN IMMEDIATE"); try { db.Execute("INSERT INTO previews(content_id,state,mime,width,height,bytes,decoder) VALUES(?,'available',?,?,?,?,?) ON CONFLICT(content_id) DO UPDATE SET state='available',mime=excluded.mime,width=excluded.width,height=excluded.height,bytes=excluded.bytes,error=NULL,decoder=excluded.decoder",content,preview.Mime,preview.Width,preview.Height,preview.Bytes,preview.Decoder); db.Execute("UPDATE jobs SET state='complete',error=NULL WHERE id=?",job.Id); Catalog.Bump(db); db.Execute("COMMIT"); } catch { Catalog.Rollback(db); throw; } return null; },preview.Bytes.Length+1024);
                    } catch(OperationCanceledException) { throw; } catch(Exception error) {
                        Write(db=> { db.Execute("INSERT INTO previews(content_id,state,error,decoder) VALUES(?,'unavailable',?,?) ON CONFLICT(content_id) DO UPDATE SET state='unavailable',error=excluded.error WHERE previews.state<>'available'",content,error.Message,"Offline preview pipeline"); return null; }); Finish(job,"failed",error);
                    }
                }
            } catch(OperationCanceledException) {} catch(Exception error) { FailWorker(error); }
        }
        void FailWorker(Exception error) { workerFailure=FileAccessNative.Describe(error); engine.Update(s=>s.Error="Worker stopped: "+workerFailure); cancellation.Cancel(); }
        void Refresh() {
            if(engine.Faulted) return;
            long shortcuts=0;
            var counts=(List<object[]>)Write(db=>{shortcuts=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM scan_users WHERE scan_id=? AND state='skipped'",id));return db.Query("SELECT kind,state,total FROM scan_counters WHERE scan_id=?",new object[]{id},30);});
            long discovered=0,done=0,failed=0,pending=0;
            foreach(object[] row in counts) {
                // "skipped" means the file was read and is not media (or is older than the cutoff); a link was never read.
                if((string)row[1]==NotFollowed) { shortcuts+=(long)row[2]; continue; }
                if((string)row[0]=="file") { discovered+=(long)row[2]; if((string)row[1]=="complete" || (string)row[1]=="skipped") done+=(long)row[2]; } if((string)row[1]=="failed") failed+=(long)row[2]; if((string)row[1]=="pending" || (string)row[1]=="processing") pending+=(long)row[2];
            }
            engine.Update(s=>{s.DiscoveredFiles=discovered;s.ProcessedFiles=done;s.FailedFiles=failed;s.PendingWork=pending;s.NotFollowed=shortcuts;s.RetainedBytes=prepared.Bytes;s.PreparedQueueCount=prepared.Count;});
        }
    }
    public sealed partial class ReviewerEngine {
        readonly object scanGate=new object(); ScanRunner scan;
        public string StartScan(ScanOptions options) {
            if(options==null) throw new ArgumentNullException("options"); ScanOptions copy=options.Copy(); copy.Validate();
            lock(scanGate) { if(!Ready || Faulted || closing || reportActive || sourceConnectionBusy || DraftReviewActive || (scan!=null && !scan.Finished)) throw new InvalidOperationException("Wait for the current scan or background work before starting a scan."); string id=Guid.NewGuid().ToString(); scan=new ScanRunner(this,copy,id,false,copy.WorkerCount); Update(s=>{s.RunningScan=true;s.CancelRequested=false;s.CompletedWithErrors=false;s.Error="";s.ScanId=id;s.Stage="Preparing scan";s.CompletedUsers=0;s.TotalUsers=0;s.BytesRead=0;s.NotFollowed=0;}); scan.Start(); return id; }
        }
        public void ResumeScan(string id,int workers) {
            if(workers<1 || workers>8) throw new ArgumentException("Choose 1–8 workers");
            lock(scanGate) { if(!Ready || Faulted || closing || reportActive || sourceConnectionBusy || DraftReviewActive || (scan!=null && !scan.Finished)) throw new InvalidOperationException("Wait for the current scan or background work before resuming."); scan=new ScanRunner(this,null,id,true,workers); Update(s=>{s.RunningScan=true;s.CancelRequested=false;s.CompletedWithErrors=false;s.Error="";s.ScanId=id;s.Stage="Loading saved manifest";s.BytesRead=0;s.NotFollowed=0;}); scan.Start(); }
        }
        public void CancelScan() { lock(scanGate) if(scan!=null && !scan.Finished) scan.Cancel(); }
        partial void StopScanner() { CancelScan(); }
        partial void JoinScanner() { ScanRunner current; lock(scanGate) current=scan; if(current!=null) current.Join(); }
    }
}
