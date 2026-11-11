using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MediaReviewer {
    // Raised when an Undo step can never apply again because its item was edited afterwards. Review
    // revisions only increase, so the caller drops the step instead of retrying it forever.
    internal sealed class ReviewChangedException:InvalidOperationException {
        internal ReviewChangedException(string message):base(message){}
    }
    public static partial class Catalog {
        internal static bool HasExtendedReviewText(SqliteConnection db,long group) {
            return db.Scalar("SELECT 1 FROM imported_fields WHERE owner_table='review_groups' AND owner_rowid=? AND column_name IN('notes','reviewer') LIMIT 1",group)!=null;
        }

        internal static long ReviewRevision(SqliteConnection db,long group) {
            return (long)db.Scalar("SELECT review_revision FROM review_groups WHERE id=?",group);
        }

        internal static Dictionary<long,long> UndoReview(SqliteConnection db,ReviewUndoItem[] items,string operation) {
            var payload=new StringBuilder("undo-review-v1\n");
            foreach(var item in items) {
                payload.Append(item.GroupId.ToString(CultureInfo.InvariantCulture)).Append('\n')
                    .Append(item.ExpectedRevision.ToString(CultureInfo.InvariantCulture)).Append('\n')
                    .Append(item.Status).Append('\n')
                    .Append(item.Reviewer.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item.Reviewer)
                    .Append(item.Notes.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item.Notes).Append('\n');
            }
            string digest=Identity.Digest(payload.ToString());
            Dictionary<long,long> revisions=null;
            Verification.Transaction(db,()=>{
                if(Verification.Reconciled(db,operation,digest))return;
                revisions=new Dictionary<long,long>(items.Length);
                string now=Now();
                foreach(var item in items) {
                    var rows=db.Query("SELECT status,legacy_key,review_revision FROM review_groups WHERE id=? AND retired=0",new object[]{item.GroupId},1);
                    if(rows.Count!=1 || (string)rows[0][0]=="EVIDENCE" || (long)rows[0][2]!=item.ExpectedRevision || HasExtendedReviewText(db,item.GroupId))
                        throw new ReviewChangedException("A selected item has changed since that review. No decisions were undone.");
                    db.Execute("UPDATE review_groups SET status=?,reviewer=?,notes=?,reviewed_at=? WHERE id=?",
                        item.Status,item.Reviewer,item.Notes,now,item.GroupId);
                    db.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",Guid.NewGuid().ToString(),item.GroupId,
                        rows[0][1],rows[0][0],item.Status,now,item.Reviewer,item.Notes,operation);
                    revisions.Add(item.GroupId,ReviewRevision(db,item.GroupId));
                }
                db.Execute("INSERT INTO operations VALUES(?,?,?,?)",operation,digest,now,Bump(db));
            });
            return revisions;
        }
    }

    public sealed partial class ReviewerEngine {
        internal const long UndoQueueLimit=1024*1024;

        internal string RequestUndoReview(ReviewUndoItem[] items,string operation) {
            RequireReportWritable();
            if(!ready || closing || faulted)throw new InvalidOperationException("Database is not ready.");
            if(items==null || items.Length<1 || items.Length>200 || String.IsNullOrEmpty(operation) || operation.Length>100 ||
                items.Any(item=>item==null || item.GroupId<1 || item.ExpectedRevision<0 ||
                    (item.Status!="TO_REVIEW" && item.Status!="COMPLIANT" && item.Status!="NON_COMPLIANT") ||
                    item.Notes==null || item.Notes.Length>16384 || item.Reviewer==null || item.Reviewer.Length>128) ||
                items.Select(item=>item.GroupId).Distinct().Count()!=items.Length)
                throw new ArgumentException("Invalid review history entry.");
            long cost=2048+items.Sum(item=>item.Cost);
            if(cost>UndoQueueLimit)throw new ArgumentException("This review history entry exceeds the Undo size limit.");
            var snapshot=items.OrderBy(item=>item.GroupId).Select(item=>item.Copy()).ToArray();
            string id=Guid.NewGuid().ToString("N");
            var work=new DbWork {
                Body=db=>{RequireReportWritable();return Catalog.UndoReview(db,snapshot,operation);},
                Done=value=>Respond(new UiResult {RequestId=id,Kind="UndoReview",OperationId=operation,
                    Success=true,Reconciled=value==null,ReviewRevisions=(Dictionary<long,long>)value}),
                Failed=error=>Respond(new UiResult {RequestId=id,Kind="UndoReview",OperationId=operation,Error=error.Message,
                    ReviewChanged=error is ReviewChangedException})
            };
            if(!controls.TryAdd(work,cost))throw new InvalidOperationException("Review queue is busy. Undo remains available.");
            return id;
        }
    }
}
