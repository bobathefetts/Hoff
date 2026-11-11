using System;
using System.IO;

namespace MediaReviewer {
    // Standard input for the private preview, playback and AI worker processes.
    //
    // The parent starts each worker with redirected input. When the parent's input encoding is UTF-8 with a
    // byte order mark (a console on code page 65001, or a UTF-8 system code page), the .NET Framework writes
    // that mark, EF BB BF, into the pipe before the parent's own first message. No worker protocol begins with
    // those three bytes: two start with a little-endian handshake integer and one with a short length-prefixed
    // format name. So a leading mark is dropped here, and anything else is passed through unchanged.
    internal static class WorkerInput {
        internal static Stream Open() { return new MarkSkippingStream(Console.OpenStandardInput()); }

        sealed class MarkSkippingStream:Stream {
            readonly Stream inner;
            readonly byte[] head=new byte[3];
            int headCount,headAt;
            bool started;

            internal MarkSkippingStream(Stream inner) { this.inner=inner; }

            public override int Read(byte[] buffer,int offset,int count) {
                if(count==0)return 0;
                if(!started) {
                    started=true;
                    // Every protocol's first message is longer than three bytes, so this cannot wait on a parent that has finished speaking.
                    while(headCount<head.Length) {
                        int n=inner.Read(head,headCount,head.Length-headCount);
                        if(n==0)break;
                        headCount+=n;
                    }
                    if(headCount==3 && head[0]==0xEF && head[1]==0xBB && head[2]==0xBF)headCount=0;
                }
                if(headAt<headCount) {
                    int n=Math.Min(count,headCount-headAt);
                    Buffer.BlockCopy(head,headAt,buffer,offset,n);
                    headAt+=n;
                    return n;
                }
                return inner.Read(buffer,offset,count);
            }

            protected override void Dispose(bool disposing) { if(disposing)inner.Dispose(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() {}
            public override long Seek(long offset,SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer,int offset,int count) { throw new NotSupportedException(); }
        }
    }
}
