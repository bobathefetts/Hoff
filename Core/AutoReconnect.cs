using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace MediaReviewer {
    public sealed class ReconnectRootResult {
        public string RootId {get;set;}public string Label {get;set;}public string State {get;set;}public string Error {get;set;}
        public long Full {get;set;}public long Candidates {get;set;}public long Missing {get;set;}public long Failed {get;set;}
    }
    public sealed class ReconnectBatch {
        public string Id="",Path="",State="none",CreatedAt="";public bool ManifestComplete;
        // Candidates counts the quick matches that are bound (accepted now, or kept because the same unchanged file was already bound); Unaccepted counts those left unverified.
        public long TotalRoots,CompletedRoots,ConnectedRoots,BlockedRoots,Full,Candidates,Unaccepted,Missing,Failed;
        public List<ReconnectRootResult> Rows=new List<ReconnectRootResult>();
        internal long Cost {get{return 512+2L*(Id.Length+Path.Length+State.Length+CreatedAt.Length)+Rows.Sum(r=>128L+2L*(r.RootId.Length+r.Label.Length+r.State.Length+r.Error.Length));}}
        public string Summary {get{return "Reconnect all: "+Full+" full matches, "+Candidates+" quick candidates bound, "+Missing+" missing, "+Failed+" failed; "+ConnectedRoots+" / "+TotalRoots+" roots connected, "+BlockedRoots+" blocked."+
            (Unaccepted>0 ? " "+Unaccepted+" quick candidate matches were not accepted and stay unverified. To accept them, run Reconnect all again with quick candidates accepted, or rescan the new location." : "")+
            " Bound quick matches remain candidates, not full-file proof. Reviews and display order are preserved.";}}
    }
    public static partial class Catalog {
        internal static ReconnectBatch ReconnectStatus(SqliteConnection db,string id,bool details=true){
            if(id==null)id=(string)db.Scalar("SELECT id FROM source_reconnect_batches ORDER BY created_at DESC,id DESC LIMIT 1");if(id==null)return new ReconnectBatch();
            var rows=db.Query("SELECT id,path,state,created_at,manifest_complete FROM source_reconnect_batches WHERE id=?",new object[]{id},1);if(rows.Count==0)throw new InvalidOperationException("This reconnect task is unavailable; refresh Reconnect all.");var r=rows[0];
            var b=new ReconnectBatch {Id=(string)r[0],Path=(string)r[1],State=(string)r[2],CreatedAt=(string)r[3],ManifestComplete=(long)r[4]!=0};
            var counts=db.Query("SELECT COUNT(*),COALESCE(SUM(state IN('connected','unmatched','blocked','discarded')),0),COALESCE(SUM(state='connected'),0),COALESCE(SUM(state='blocked'),0),COALESCE(SUM(CASE WHEN state='connected' THEN full_count ELSE 0 END),0),COALESCE(SUM(CASE WHEN state='connected' THEN candidate_count ELSE 0 END),0),COALESCE(SUM(missing_count),0),COALESCE(SUM(failed_count),0) FROM source_reconnect_items WHERE batch_id=?",new object[]{id},1)[0];
            b.TotalRoots=(long)counts[0];b.CompletedRoots=(long)counts[1];b.ConnectedRoots=(long)counts[2];b.BlockedRoots=(long)counts[3];b.Full=(long)counts[4];b.Candidates=(long)counts[5];b.Missing=(long)counts[6];b.Failed=(long)counts[7];
            // A connected item records only the candidates it bound; its source check still holds how many were found.
            // An unmatched item records the candidates that were found but not accepted.
            b.Unaccepted=Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(CASE WHEN i.state='connected' THEN MAX(COALESCE(c.candidate_count,i.candidate_count)-i.candidate_count,0) WHEN i.state IN('cleaning_unmatched','unmatched') THEN i.candidate_count ELSE 0 END),0) FROM source_reconnect_items i LEFT JOIN source_connections c ON c.id=i.connection_id WHERE i.batch_id=?",id));
            if(details)foreach(var row in db.Query("SELECT i.root_id,substr(r.label,1,1024),i.state,i.full_count,i.candidate_count,i.missing_count,i.failed_count,substr(i.error,1,2048) FROM source_reconnect_items i JOIN roots r ON r.id=i.root_id WHERE i.batch_id=? ORDER BY CASE WHEN i.state IN('checking','cleaning_unmatched','cleaning_blocked') THEN 0 WHEN i.state='pending' THEN 2 ELSE 1 END,i.root_order DESC LIMIT 60",new object[]{id},60))b.Rows.Add(new ReconnectRootResult {RootId=(string)row[0],Label=(string)row[1],State=(string)row[2],Full=(long)row[3],Candidates=(long)row[4],Missing=(long)row[5],Failed=(long)row[6],Error=(string)row[7]});
            return b;
        }
    }
    public sealed partial class ReviewerEngine {
        // A quick candidate matches on sampled windows only, so it is not full-file proof. Reconnect all binds
        // candidates only when the person starting or resuming the task accepts that explicitly, as the
        // single-root path in SourceConnections requires. Otherwise it binds full matches and leaves the rest unverified.
        public string RequestReconnectAll(string rootPath,bool acceptCandidates=false){
            string path=Path.GetDirectoryName(Catalog.PreviewSourcePath(rootPath,"source-check")),id=Guid.NewGuid().ToString("N");
            return EnqueueOperationWithId("ReconnectAll",(db,t,p)=>OwnSourceConnection(()=>ReconnectAllCore(db,id,path,acceptCandidates,t,p)),4096+path.Length*2L,null,id);
        }
        public string RequestResumeReconnectAll(string batchId,bool acceptCandidates=false){string id=ConnectionId(batchId);return EnqueueOperation("ReconnectAll",(db,t,p)=>OwnSourceConnection(()=>ReconnectAllCore(db,id,null,acceptCandidates,t,p)));}
        public string RequestReconnectAllStatus(string batchId=null){string id=batchId==null ? null : ConnectionId(batchId);return QueueSourceRead("ReconnectAllStatus",db=>id!=null && db.Scalar("SELECT 1 FROM source_reconnect_batches WHERE id=?",id)==null ? new ReconnectBatch {Id=id,State="not_saved"} : Catalog.ReconnectStatus(db,id));}
        public string RequestDiscardReconnectAll(string batchId){string id=ConnectionId(batchId);return EnqueueOperation("ReconnectAllDiscard",(db,t,p)=>OwnSourceConnection(()=>DiscardReconnectAll(db,id,t,p)));}
        void TrimReconnectHistory(SqliteConnection db,CancellationToken token){
            while(true){token.ThrowIfCancellationRequested();string old=(string)db.Scalar("SELECT id FROM source_reconnect_batches WHERE state IN('complete','complete_errors','discarded') ORDER BY created_at DESC,id DESC LIMIT 1 OFFSET 4");if(old==null)return;
                long removed=(long)WaitWrite(w=>(long)w.Execute("DELETE FROM source_reconnect_items WHERE rowid IN(SELECT rowid FROM source_reconnect_items WHERE batch_id=? LIMIT 200)",old));if(removed==0)WaitWrite(w=>{w.Execute("DELETE FROM source_reconnect_batches WHERE id=?",old);return null;});
            }
        }
        void ReconnectManifest(SqliteConnection db,string id,CancellationToken token,Action<long,long,string> progress){
            while(true){token.ThrowIfCancellationRequested();var header=db.Query("SELECT manifest_complete,manifest_cursor,root_ceiling FROM source_reconnect_batches WHERE id=?",new object[]{id},1)[0];if((long)header[0]!=0)return;
                var roots=db.Query("SELECT r.rowid,substr(r.id,1,4097) FROM roots r WHERE r.rowid>? AND r.rowid<=? AND EXISTS(SELECT 1 FROM occurrences o WHERE o.root_id=r.id AND "+Catalog.ConnectionEligible+") ORDER BY r.rowid LIMIT 32",new object[]{header[1],header[2]},32);
                if(roots.Any(r=>((string)r[1]).Length>4096))throw new InvalidDataException("A recorded root identifier exceeds the reconnection limit. Its data is preserved; automatic reconnection stopped without truncating identifiers.");
                WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{foreach(var root in roots)w.Execute("INSERT OR IGNORE INTO source_reconnect_items(batch_id,root_id,root_order) VALUES(?,?,?)",id,root[1],root[0]);if(roots.Count==0)w.Execute("UPDATE source_reconnect_batches SET manifest_complete=1 WHERE id=?",id);else w.Execute("UPDATE source_reconnect_batches SET manifest_cursor=? WHERE id=?",roots[roots.Count-1][0],id);});return null;},4096+roots.Sum(x=>128L+((string)x[1]).Length*2L));
                progress(Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM source_reconnect_items WHERE batch_id=?",id)),-1,"source roots recorded");
            }
        }
        string ReconnectAllCore(SqliteConnection db,string id,string newPath,bool acceptCandidates,CancellationToken token,Action<long,long,string> progress){
            token.ThrowIfCancellationRequested();if(newPath!=null){TrimReconnectHistory(db,token);WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{
                if(w.Scalar("SELECT 1 FROM source_reconnect_batches WHERE state IN('running','interrupted','discarding') LIMIT 1")!=null)throw new InvalidOperationException("Resume or discard the unfinished Reconnect all task before starting another.");
                w.Execute("INSERT INTO source_reconnect_batches(id,path,state,created_at,root_ceiling) VALUES(?,?,'running',?,(SELECT COALESCE(MAX(rowid),0) FROM roots))",id,newPath,Catalog.Now());
            });return null;});}
            var b=Catalog.ReconnectStatus(db,id,false);if(b.State=="complete" || b.State=="complete_errors" || b.State=="discarded")return b.Summary;if(b.State=="discarding")throw new InvalidOperationException("This task is being discarded; choose Discard unfinished to finish it.");
            string path=b.Path;WaitWrite(w=>{w.Execute("UPDATE source_reconnect_batches SET state='running' WHERE id=?",id);return null;});
            try{
                ReconnectManifest(db,id,token,progress);b=Catalog.ReconnectStatus(db,id,false);
                while(true){token.ThrowIfCancellationRequested();var pending=db.Query("SELECT root_id,state,connection_id FROM source_reconnect_items WHERE batch_id=? AND state IN('pending','checking','cleaning_unmatched','cleaning_blocked') ORDER BY root_order LIMIT 1",new object[]{id},1);if(pending.Count==0)break;
                    var row=pending[0];string root=(string)row[0],state=(string)row[1],connection=(string)row[2];long ordinal=Catalog.ReconnectStatus(db,id,false).CompletedRoots+1;
                    if(state.StartsWith("cleaning_",StringComparison.Ordinal)){if(!PreserveAppliedReconnectRoot(id,root,connection)){CleanReconnectCheck(db,connection,token);FinishReconnectCleanup(id,root,state=="cleaning_blocked" ? "blocked" : "unmatched");}continue;}
                    if(connection==null){
                        if(db.Scalar("SELECT 1 FROM source_connections WHERE root_id=? AND state IN('checking','interrupted','ready','discarding') LIMIT 1",root)!=null){BlockReconnectRoot(id,root,"An earlier manual source check is unfinished. Resume or discard it in Sources / reconnect.");continue;}
                        connection=Guid.NewGuid().ToString("N");WaitWrite(w=>{w.Execute("UPDATE source_reconnect_items SET connection_id=?,state='checking' WHERE batch_id=? AND root_id=?",connection,id,root);return null;});
                    }
                    try{
                        bool exists=db.Scalar("SELECT 1 FROM source_connections WHERE id=?",connection)!=null;
                        CheckConnection(db,connection,exists ? null : root,exists ? null : path,token,(done,total,unit)=>progress(done,total,"root "+ordinal+" / "+b.TotalRoots+": "+unit));token.ThrowIfCancellationRequested();
                        var checkedRoot=Catalog.ConnectionStatus(db,connection,false);
                        WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{
                            // A check the person already applied in Sources carries its own explicit acceptance; never rewrite it.
                            bool applied=(string)w.Scalar("SELECT state FROM source_connections WHERE id=?",connection)=="applied";
                            // Without acceptance, a candidate stays bound only where the same unchanged file at the same location
                            // was already bound (accepted before, or read by a later scan), so running Reconnect all again revokes nothing.
                            long bound=applied || acceptCandidates ? checkedRoot.Candidates : Convert.ToInt64(w.Scalar("SELECT COUNT(*) FROM source_connection_files WHERE connection_id=?1 AND state='candidate' AND "+Catalog.CandidateAcceptedBefore,connection));
                            bool connect=applied || checkedRoot.Full+bound>0;
                            if(connect && !applied){
                                // bound_occurrences binds 'full' and 'candidate' rows. An unaccepted candidate is renamed and
                                // loses its observed file details, so it stays recorded but unverified like a file that failed the check.
                                if(!acceptCandidates)w.Execute("UPDATE source_connection_files SET state='unaccepted',size=NULL,modified_ticks=NULL,file_identity=NULL WHERE connection_id=?1 AND state='candidate' AND NOT "+Catalog.CandidateAcceptedBefore,connection);
                                ApplyConnection(w,connection,true,true);
                            }
                            w.Execute("UPDATE source_reconnect_items SET state=?,full_count=?,candidate_count=?,missing_count=?,failed_count=? WHERE batch_id=? AND root_id=?",connect ? "connected" : "cleaning_unmatched",checkedRoot.Full,connect ? bound : checkedRoot.Candidates,checkedRoot.Missing,checkedRoot.Failed,id,root);Catalog.Bump(w);
                        });return null;});
                    }catch(InvalidOperationException failure){
                        string error=failure.Message.Length>2048 ? failure.Message.Substring(0,2048) : failure.Message;
                        WaitWrite(w=>{w.Execute("UPDATE source_reconnect_items SET state='cleaning_blocked',error=? WHERE batch_id=? AND root_id=?",error,id,root);Catalog.Log(w,"warning","Reconnect all",path,"Root "+root+": "+error);return null;});
                    }
                    var current=Catalog.ReconnectStatus(db,id,false);progress(current.CompletedRoots,current.TotalRoots,"source roots finished");
                }
                var final=Catalog.ReconnectStatus(db,id,false);WaitWrite(w=>{w.Execute("UPDATE source_reconnect_batches SET state=? WHERE id=?",final.Missing+final.Failed+final.BlockedRoots>0 ? "complete_errors" : "complete",id);Catalog.Log(w,"info","Reconnect all",path,final.Summary);return null;});return final.Summary;
            }catch{if(!faulted && !stop.IsCancellationRequested)try{WaitWrite(w=>{w.Execute("UPDATE source_reconnect_batches SET state='interrupted' WHERE id=? AND state='running'",id);return null;});}catch{}throw;}
        }
        void BlockReconnectRoot(string batch,string root,string error){WaitWrite(w=>{w.Execute("UPDATE source_reconnect_items SET state='blocked',error=? WHERE batch_id=? AND root_id=?",error,batch,root);Catalog.Log(w,"warning","Reconnect all",root,error);return null;});}
        void CleanReconnectCheck(SqliteConnection db,string id,CancellationToken token){
            if(id==null)return;if(db.Scalar("SELECT 1 FROM root_source_heads WHERE connection_id=?",id)!=null)throw new InvalidOperationException("An active source connection cannot be discarded.");
            while(true){token.ThrowIfCancellationRequested();long removed=(long)WaitWrite(w=>(long)w.Execute("DELETE FROM source_connection_files WHERE rowid IN(SELECT rowid FROM source_connection_files WHERE connection_id=? LIMIT 200)",id));if(removed==0)break;}
            WaitWrite(w=>{w.Execute("DELETE FROM source_connections WHERE id=? AND NOT EXISTS(SELECT 1 FROM root_source_heads WHERE connection_id=?)",id,id);return null;});
        }
        void FinishReconnectCleanup(string batch,string root,string state){WaitWrite(w=>{w.Execute("UPDATE source_reconnect_items SET state=?,connection_id=NULL WHERE batch_id=? AND root_id=?",state,batch,root);return null;});}
        bool PreserveAppliedReconnectRoot(string batch,string root,string connection){
            if(connection==null)return false;return (bool)WaitWrite(w=>{bool applied=false;Catalog.PreviewTransaction(w,()=>{
                if(w.Scalar("SELECT 1 FROM root_source_heads WHERE root_id=? AND connection_id=?",root,connection)==null)return;
                var s=Catalog.ConnectionStatus(w,connection,false);w.Execute("UPDATE source_reconnect_items SET state='connected',full_count=?,candidate_count=?,missing_count=?,failed_count=?,error='' WHERE batch_id=? AND root_id=?",s.Full,s.Candidates,s.Missing,s.Failed,batch,root);Catalog.Bump(w);applied=true;
            });return (object)applied;});
        }
        string DiscardReconnectAll(SqliteConnection db,string id,CancellationToken token,Action<long,long,string> progress){
            var batch=Catalog.ReconnectStatus(db,id,false);if(batch.State=="complete" || batch.State=="complete_errors" || batch.State=="discarded")return "This Reconnect all task is already finished; committed connections remain active.";
            WaitWrite(w=>{w.Execute("UPDATE source_reconnect_batches SET state='discarding' WHERE id=?",id);return null;});long done=0;
            while(true){token.ThrowIfCancellationRequested();var rows=db.Query("SELECT root_id,connection_id FROM source_reconnect_items WHERE batch_id=? AND state IN('pending','checking','cleaning_unmatched','cleaning_blocked') ORDER BY root_order LIMIT 1",new object[]{id},1);if(rows.Count==0)break;string root=(string)rows[0][0],connection=(string)rows[0][1];if(!PreserveAppliedReconnectRoot(id,root,connection)){CleanReconnectCheck(db,connection,token);FinishReconnectCleanup(id,root,"discarded");}progress(++done,-1,"unfinished roots reconciled or discarded");}
            WaitWrite(w=>{w.Execute("UPDATE source_reconnect_batches SET state='discarded' WHERE id=?",id);return null;});return "Unfinished reconnection work discarded. Committed connections, saved media and reviews are preserved.";
        }
    }
}
