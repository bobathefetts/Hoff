namespace MediaReviewer {
    public static class Product {
        public const string Version="0.15.5";
        public const string RevisionDate="2026-10-22";
        public const string Author="IT1 Charlie McPherson";
        public const string DotNetVersion="0.3.23-preview";
        public const string Contributors="IT1 Joshua Maulden and IT2 Alexander Harris";
        public const string EventCredit="Made at FleetHacker 2026";
        public const string DotNetDate="2026-11-06";
        // The name the running exe actually has, for messages that tell the user where to put files.
        public static string ExecutableName {
            get {
                try{using(var self=System.Diagnostics.Process.GetCurrentProcess())return System.IO.Path.GetFileName(self.MainModule.FileName);}
                catch(System.Exception){return "Media Compliance Reviewer.exe";}
            }
        }
    }
}
