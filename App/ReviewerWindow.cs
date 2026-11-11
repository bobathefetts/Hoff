using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MediaReviewer {
    sealed partial class ReviewerWindow:IDisposable {
        static readonly string[] BucketNames={"To be reviewed","Compliant","Non-compliant","Evidence","AI suggestions"};
        static readonly string[] BucketIcons={"📥  ","🌿  ","🚩  ","🔒  ","◇  "};
        static readonly string[] BucketKeys={"TO_REVIEW","COMPLIANT","NON_COMPLIANT","EVIDENCE",DraftReview.Bucket};
        readonly ObservableCollection<CardItem> cards=new ObservableCollection<CardItem>();
        readonly Dictionary<string,Action<UiResult>> pending=new Dictionary<string,Action<UiResult>>();
        readonly HashSet<Window> databaseWindows=new HashSet<Window>();
        readonly GallerySelection selection;
        readonly MediaLoader loader=new MediaLoader();
        readonly DispatcherTimer timer;
        readonly ViewGeneration view=new ViewGeneration();
        readonly ViewGeneration inspection=new ViewGeneration();
        readonly ReviewPreferences options=new ReviewPreferences();
        readonly List<UndoEntry> undoStack=new List<UndoEntry>();
        sealed class UndoEntry {
            internal ReviewUndoItem[] Items;
            internal string OperationId;
            internal long Cost;
        }
        bool undoInFlight;
        ReviewerEngine engine,closingDatabase;
        StatusSnapshot status;
        string pageRequest,task,draftTask,preferencesSignature;
        ReviewPreferences savedPreferences;
        bool closing,allowClose,refresh,loadedPreferences,waitingOnClosePreference,switchingCatalog;
        long pageCommitVersion;
        string pageSearch="",pageFolder="";
        DateTime? databaseClosedAt;
        string gemmaCriteria="";
        int bucket,page=1,pageCount=1,marqoThreshold=DraftReview.DefaultMarqoThreshold,gemmaThreshold=DraftReview.DefaultGemmaThreshold;
        bool gemmaAvailable,useGemma,gemmaCpuOnly,gemmaSettingsLoaded;
        long revision=-1;
        Window inspector;
        Action<MediaLoadResult> showInspection;

        internal Window Window {get;private set;}
        internal bool HasRenderedPreview {get{return cards.Any(c=>c.Image!=null) && Get<FrameworkElement>("PreviewPane").ActualWidth>0;}}
        T Get<T>(string name) where T:class {return (T)Window.FindName(name);}
        Button Button(string name){return Get<Button>(name);}
        ComboBox Combo(string name){return Get<ComboBox>(name);}
        TextBox Box(string name){return Get<TextBox>(name);}
        void Text(string name,string value){Get<TextBlock>(name).Text=value??"";}
        void Message(string value){Text("MessageText",value);}
        bool Ready {get{return engine!=null && engine.Ready && !engine.Faulted && !closing && !switchingCatalog;}}
        internal bool IsClosing {get{return closing;}}
        // The app no longer asks for a catalog at startup. The first scan creates this one, and later launches reopen it.
        // It lives in the user's local profile because a catalog must be on local storage that no sync client rewrites.
        internal static string DefaultCatalogOverride;
        internal static string DefaultCatalogPath {get{
            if(DefaultCatalogOverride!=null)return DefaultCatalogOverride;
            string profile=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(String.IsNullOrEmpty(profile))profile=AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(profile,"Media Compliance Reviewer","Media Review Catalog.sqlite");
        }}
        // The scan that is waiting for the default catalog to finish opening.
        Action pendingStart;
        bool pageBoxEditing;
        string pageBoxShown="1";
        internal string CatalogPath {get{return engine==null ? null : engine.DatabasePath;}}
        internal bool ScanSettled {get{return Ready && pendingStart==null && status!=null && !status.RunningScan && status.ProcessedFiles>0;}}
        internal void OpenDefaultCatalogIfPresent() {
            string path=DefaultCatalogPath;
            if(File.Exists(path))Guard(()=>Open(path,false));
        }
        void RunPendingStart(){var start=pendingStart;pendingStart=null;if(start!=null)Guard(start);}
        static string Id(){return Guid.NewGuid().ToString("N");}

        internal ReviewerWindow(Window window,bool persistWorkspace=true) {
            Window=window;
            var list=Get<ItemsControl>("CardList");list.ItemsSource=cards;
            list.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler(CardClick));
            selection=new GallerySelection(Get<ScrollViewer>("GalleryScroll"),list,cards);
            selection.Changed+=(s,e)=>UpdateControls();
            timer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(100)};
            // The window smoke test runs the tick unguarded so a regression fails the run with a stack trace. The
            // shutdown sequence is unguarded too: a swallowed fault there would leave a window that can never close.
            timer.Tick+=(s,e)=>{if(persistWorkspace && !closing)Guard(Tick);else Tick();};timer.Start();
            window.Closing+=Closing;
            Click("CatalogButton",ChooseDatabase);
            Click("BrowseButton",()=>{string selected=ChooseFolder("Choose a media source",Box("SourceBox").Text);if(selected!=null)Box("SourceBox").Text=selected;});
            Click("StartButton",StartScan);Click("ResumeButton",()=>engine.ResumeScan(status.ResumeId,Number("WorkerBox")));
            Click("CancelButton",()=>engine.CancelScan());
            Click("AiDraftButton",ShowDraftReview);
            Click("FormatsButton",ChooseFormats);Click("OptionsButton",ChooseOptions);
            foreach(FormatFamily family in Formats.Families)Combo("TypeBox").Items.Add(new ComboBoxItem {Content=family.Label,Tag=family.Key});
            Combo("ViewBox").SelectionChanged+=(s,e)=>{if(Combo("ViewBox").SelectedIndex>=0){bucket=Combo("ViewBox").SelectedIndex;NewPage(1);}};
            Click("FilterButton",()=>NewPage(1));Click("ResetButton",ResetView);
            Click("PreviousButton",()=>NewPage(page-1));Click("NextButton",()=>NewPage(page+1));Click("GoButton",GoToPage);
            var pageBox=Box("PageBox");var goButton=Button("GoButton");
            pageBox.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){e.Handled=true;Guard(GoToPage);}};
            // Typing a page number: the first click selects the old number so the new one replaces it, and the entry is
            // kept (through the click on Jump) until it is used or the reviewer moves on to something else.
            pageBox.GotKeyboardFocus+=(s,e)=>{pageBoxEditing=true;pageBox.SelectAll();};
            pageBox.PreviewMouseLeftButtonDown+=(s,e)=>{if(!pageBox.IsKeyboardFocusWithin){e.Handled=true;pageBox.Focus();}};
            pageBox.LostKeyboardFocus+=(s,e)=>{if(!ReferenceEquals(e.NewFocus,goButton))EndPageEdit();};
            goButton.LostKeyboardFocus+=(s,e)=>{if(!ReferenceEquals(e.NewFocus,pageBox))EndPageEdit();};
            Box("SearchBox").KeyDown+=(s,e)=>{if(e.Key==Key.Enter){e.Handled=true;NewPage(1);}};
            Combo("PageSizeBox").SelectionChanged+=(s,e)=>NewPage(1);
            Combo("SortBox").SelectionChanged+=(s,e)=>{columnSort=null;NewPage(1);};
            Combo("TypeBox").SelectionChanged+=(s,e)=>NewPage(1);
            Combo("SmartFilterBox").SelectionChanged+=(s,e)=>NewPage(1);
            Combo("FitBox").SelectionChanged+=(s,e)=>SetFit();
            Click("SelectPageButton",()=>SelectAll(true));Click("ClearSelectionButton",()=>SelectAll(false));
            Click("CompliantButton",()=>ReviewWorkspaceItems("COMPLIANT"));Click("UnreviewedButton",()=>ReviewWorkspaceItems("TO_REVIEW"));
            Click("NonCompliantButton",()=>ReviewWorkspaceItems("NON_COMPLIANT"));Click("CaptureEvidenceButton",()=>ReviewWorkspaceItems("EVIDENCE"));
            Click("UndoReviewButton",UndoLastReview);
            foreach(string tagBtn in new[]{"NoteTagPersonalButton","NoteTagUnauthorizedButton","NoteTagRenamedButton","NoteTagGameButton","NoteTagFollowUpButton"}){
                string btnName=tagBtn;
                Click(btnName,()=>AppendNoteTag(Box("NotesBox"),(string)Button(btnName).Tag));
            }
            Click("ClearNotesButton",()=>Box("NotesBox").Clear());
            Click("RetryPreviewsButton",()=>Work(engine.RequestPreviewRetry(Selected().Select(c=>c.Record.GroupId).ToArray()),"Preview retry"));
            Click("VaultButton",()=>{if(engine.VaultUnlocked){if(protectedWindow!=null)protectedWindow.Close();Work(engine.RequestVaultLock(),"Lock originals");}else Unlock(null);});
            Click("StoredOriginalButton",()=>Original(Selected().Single().Record));
            Click("ExportOriginalButton",()=>ExportOriginal(Selected().Single().Record));
            Click("RetainOriginalsButton",RetainOriginals);
            Click("MaintenanceButton",Maintenance);
            Click("PreviewTasksButton",PreviewTasks);
            Click("VerifyButton",()=>Verify(Selected().Single().Record));
            Click("IdentityHistoryButton",()=>Work(engine.RequestIdentityHistory(Selected().Single().Record.GroupId),"Identity history",r=>ShowText("Identity history",r.Value)));
            Click("LogButton",()=>Queue(engine.RequestLog(),r=>{if(r.Success)ShowText("Log",r.Value);else Message(r.Error);}));
            Click("CancelWorkButton",()=>{string active=draftTask??task;if(active!=null)engine.CancelOperation(active);});
            Click("RetryPreferencesButton",LoadPreferences);
            Click("ShutdownRetryButton",SaveBeforeClose);
            Click("ShutdownKeepSavedButton",()=>{waitingOnClosePreference=false;engine.CloseAsync();});
            Click("HelpButton",()=>ShowText("Help","Create or open a database on a local drive. Choose a local folder, mapped drive or UNC media source. The sidebar holds the review queues, view modes and catalog tools. Use the arrows at the panel edges to collapse or reopen them. Drag the thin preview divider immediately to the left of its arrow to resize it.\n\nDouble-click an item to open Inspect without changing the view. Choose Quick Review in the sidebar for directional review. Swipe or drag left for the previous item, right for the next item, up for Compliant, or down for Non-compliant. The arrow keys do the same when the image is focused. Use Details / notes or Inspect for a closer look.\n\nQuick scans common images and video. Deep also includes RAW and AppData. Use Resume scan after a cancellation.\n\nSaved previews and database metadata are not encrypted. The originals vault stores encrypted retained originals."));
            Click("AboutButton",About);
            SetUpWorkspace(persistWorkspace);
            SetUpCommandSections();
            UpdateControls();
        }

        void Click(string name,Action action){Button(name).Click+=(s,e)=>Guard(action);}
        void SetUpCommandSections() {
            Action summarize=()=>{
                var modeItem=Combo("ModeBox").SelectedItem as ComboBoxItem;string mode=modeItem==null ? "" : (string)modeItem.Content;
                int workers=Number("WorkerBox");
                string age=Box("AgeBox").Text.Trim();
                Get<Expander>("ScanSettingsExpander").Header="Scan settings · "+mode+" · "+workers+" workers"+(age=="0" ? "" : " · age limit");
                Button("ScanCogButton").Content="⚙ "+mode+" · "+workers+"w"+(age=="0" ? "" : " · ≤"+age+"y");
                var type=Combo("TypeBox").SelectedItem as ComboBoxItem;
                var smart=Combo("SmartFilterBox").SelectedItem as ComboBoxItem;
                var sort=Combo("SortBox").SelectedItem as ComboBoxItem;
                Get<Expander>("FiltersExpander").Header="Filters and display · "+(type==null ? "All types" : type.Content)+(smart!=null && !String.IsNullOrEmpty((string)smart.Tag) ? " · "+smart.Content : "")+" · "+(sort==null ? "First found" : sort.Content)+(String.IsNullOrWhiteSpace(Box("FolderBox").Text) ? "" : " · folder filter");
                Get<Expander>("ReviewNotesExpander").Header="Reviewer and notes · "+(String.IsNullOrWhiteSpace(Box("ReviewerBox").Text) ? "No reviewer label" : Box("ReviewerBox").Text)+(String.IsNullOrWhiteSpace(Box("NotesBox").Text) ? "" : " · notes entered");
            };
            foreach(string name in new[]{"ModeBox","WorkerBox","TypeBox","SmartFilterBox","SortBox"})Combo(name).SelectionChanged+=(s,e)=>Guard(summarize);
            foreach(string name in new[]{"AgeBox","FolderBox","ReviewerBox","NotesBox"})Box(name).TextChanged+=(s,e)=>Guard(summarize);
            Box("FolderBox").KeyDown+=(s,e)=>{if(e.Key==Key.Enter){e.Handled=true;NewPage(1);}};
            // Keep a useful gallery area even with several sections open on a short screen.
            Window.SizeChanged+=(s,e)=>Get<FrameworkElement>("TopControls").MaxHeight=Math.Max(220,Math.Min(400,Window.ActualHeight*0.44));
            Guard(summarize);
        }
        void Guard(Action action){try{action();}catch(Exception e){Console.Error.WriteLine(e);Message(e.GetBaseException().Message);}}
        void Queue(string id,Action<UiResult> action){pending.Add(id,action);}
        void Work(string id,string label,Action<UiResult> complete=null,bool notifyOnFailure=false) {
            task=id;Message(label+" queued…");
            Queue(id,r=>{if(task==id)task=null;Message(r.Success ? (r.Value??(r.Reconnection==null ? label+" complete." : r.Reconnection.Summary)) : label+" failed: "+r.Error);if(complete!=null && (r.Success || notifyOnFailure))complete(r);});
        }

        void ChooseDatabase() {
            var picker=new OpenFileDialog {
                Title="Select an existing catalog or enter a new filename",
                Filter="Catalogs and Evidence packages (*.sqlite;*.db;*.mcrevidence)|*.sqlite;*.db;*.mcrevidence|All files (*.*)|*.*",
                CheckFileExists=false, CheckPathExists=true, DefaultExt=".sqlite"
            };
            if(picker.ShowDialog(Window)!=true)return;
            string path=picker.FileName;
            if(String.Equals(Path.GetExtension(path),".mcrevidence",StringComparison.OrdinalIgnoreCase))OpenPackage(path);
            else Open(path,IsNewCatalogPath(path));
        }

        internal static bool IsNewCatalogPath(string path) {
            try {
                if((File.GetAttributes(Paths.Io(path)) & FileAttributes.Directory)!=0)
                    throw new ArgumentException("Select a catalog file or enter a new filename.");
                return false;
            } catch(FileNotFoundException) { return true; }
        }

        internal void Open(string path,bool create) {
            if(closing || closingDatabase!=null || engine!=null && ((!Ready && !engine.Faulted) || status!=null && status.RunningScan || pending.Count!=0 || task!=null))throw new InvalidOperationException("Wait for current work to finish before changing databases.");
            if(engine==null){StartOpen(path,create,null);return;}
            var old=engine;string save=SavePreferences(old);
            if(save!=null){
                string signature=options.Signature;
                switchingCatalog=true;Message("Saving preferences before changing catalogs…");UpdateControls();
                Queue(save,r=>{
                    switchingCatalog=false;
                    if(r.Success)preferencesSignature=signature;
                    if(closing){if(r.Success)SaveBeforeClose();else PreferenceCloseFailed(r.Error);return;}
                    if(r.Success)SwitchCatalog(old,path,create);
                    else Message("Could not save preferences. The current catalog is still open. "+r.Error);
                });
                return;
            }
            SwitchCatalog(old,path,create);
        }

        void SwitchCatalog(ReviewerEngine old,string path,bool create) {
            if(inspector!=null)inspector.Close();
            if(protectedWindow!=null)protectedWindow.Close();
            CloseDatabaseWindows();
            closingDatabase=old;engine=null;UpdateControls();Text("ActivityText","Closing the current database…");
            old.CloseAsync();
            var wait=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
            wait.Tick+=(s,e)=>{if(old.Closed){wait.Stop();closingDatabase=null;if(!closing)Guard(()=>StartOpen(path,create,null));}};
            wait.Start();
        }

        void StartOpen(string path,bool create,string html) {
            if(inspector!=null)inspector.Close();
            if(protectedWindow!=null)protectedWindow.Close();
            pendingStart=null;
            ClearCards();pending.Clear();undoStack.Clear();view.Advance();page=1;quickIndex=0;bucket=0;revision=-1;status=null;pageRequest=null;
            Combo("ViewBox").SelectedIndex=0;
            marqoThreshold=DraftReview.DefaultMarqoThreshold;gemmaThreshold=DraftReview.DefaultGemmaThreshold;gemmaCriteria="";useGemma=false;gemmaCpuOnly=false;gemmaSettingsLoaded=false;gemmaAvailable=DraftModelWorker.GemmaAvailable();
            pageSearch=Box("SearchBox").Text;pageFolder=Box("FolderBox").Text;
            refresh=true;loadedPreferences=false;preferencesSignature=null;savedPreferences=null;offeredImport=false;
            options.DefaultSourceRoot="";Box("SourceBox").Clear();
            engine=html==null ? ReviewerEngine.Open(path,create) : ReviewerEngine.ImportHtml(html,path);
            Text("DatabaseLabel",path);Text("ActivityText",html!=null ? "Importing HTML database…" : "Opening database…");
            Text("PreferencesText","Loading saved preferences…");UpdateControls();
        }

        void LoadPreferences() {
            Button("RetryPreferencesButton").Visibility=Visibility.Hidden;
            Queue(engine.RequestPreferences(),r=>{
                if(!r.Success){Text("PreferencesText","Preferences unavailable: "+r.Error);Button("RetryPreferencesButton").Visibility=Visibility.Visible;RunPendingStart();return;}
                savedPreferences=r.Preferences;preferencesSignature=r.Preferences.Signature;
                Combo("PageSizeBox").SelectedItem=r.Preferences.PageSize;
                Combo("WorkerBox").SelectedItem=r.Preferences.WorkerCount;
                Combo("FitBox").SelectedIndex=r.Preferences.Fit=="UniformToFill" ? 1 : 0;
                Combo("ModeBox").SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"Quick","Deep","Custom"},r.Preferences.Mode));
                Box("ReviewerBox").Text=String.IsNullOrEmpty(r.Preferences.Reviewer) ? WindowsReviewerLabel() : r.Preferences.Reviewer;Box("AgeBox").Text=r.Preferences.ModifiedAgeYears.ToString(CultureInfo.InvariantCulture);
                options.Families=r.Preferences.Families;options.IncludeApplicationData=r.Preferences.IncludeApplicationData;
                options.IncludeRootFiles=r.Preferences.IncludeRootFiles;options.ScanArchives=r.Preferences.ScanArchives;
                options.SniffRenamed=r.Preferences.SniffRenamed;options.FullHashScans=r.Preferences.FullHashScans;
                options.DefaultSourceRoot=r.Preferences.DefaultSourceRoot;
                if(String.IsNullOrWhiteSpace(Box("SourceBox").Text))Box("SourceBox").Text=options.DefaultSourceRoot;
                Text("PreferencesText",String.IsNullOrEmpty(r.Preferences.Warning) ? "Preferences loaded for this database." : r.Preferences.Warning);
                RunPendingStart();
            });
        }

        string SavePreferences(ReviewerEngine target) {
            if(savedPreferences==null || savedPreferences.ReadOnly || !target.Ready || target.Faulted)return null;
            options.PageSize=Number("PageSizeBox");options.WorkerCount=Number("WorkerBox");options.ModifiedAgeYears=Age();
            options.Fit=Tag("FitBox");options.Mode=Mode();options.Reviewer=Box("ReviewerBox").Text;
            return options.Signature==preferencesSignature ? null : target.RequestSavePreferences(options,Id());
        }

        int Number(string name){return (int)Combo(name).SelectedItem;}
        string Tag(string name){return (string)((ComboBoxItem)Combo(name).SelectedItem).Tag;}
        string Mode(){return (string)((ComboBoxItem)Combo("ModeBox").SelectedItem).Content;}
        int Age(){int age;if(!Int32.TryParse(Box("AgeBox").Text,out age) || age<0 || age>ScanOptions.MaxModifiedAgeYears)throw new ArgumentException("Enter an age from 0 to "+ScanOptions.MaxModifiedAgeYears+" years.");return age;}
        void StartScan() {
            string mode=Mode();
            var request=new ScanOptions {RootPath=Box("SourceBox").Text.Trim(),WorkerCount=Number("WorkerBox"),ModifiedAgeYears=Age(),
                ExcludeApplicationData=mode!="Deep" && !options.IncludeApplicationData,IncludeRootFiles=options.IncludeRootFiles,ScanArchives=options.ScanArchives,SniffRenamed=options.SniffRenamed,
                FingerprintPolicy=options.FullHashScans ? FingerprintPolicy.Full() : new FingerprintPolicy(),
                Extensions=mode=="Quick" ? Formats.Quick : mode=="Deep" ? Formats.Deep : options.Families.SelectMany(Formats.Filter).ToArray()};
            if(engine!=null){engine.StartScan(request);Message("Scan queued…");return;}
            // No catalog is open: create the default one (or reopen it) and start this scan as soon as it is ready.
            if(String.IsNullOrWhiteSpace(request.RootPath))throw new ArgumentException("Choose a folder to scan first.");
            string path=DefaultCatalogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Opening a catalog loads its saved preferences into these controls. Keep what is on screen for this scan.
            string source=Box("SourceBox").Text,age=Box("AgeBox").Text;int modeIndex=Combo("ModeBox").SelectedIndex;object workers=Combo("WorkerBox").SelectedItem;
            var chosen=options.Copy();
            StartOpen(path,IsNewCatalogPath(path),null);
            pendingStart=()=>{
                Box("SourceBox").Text=source;Box("AgeBox").Text=age;Combo("ModeBox").SelectedIndex=modeIndex;Combo("WorkerBox").SelectedItem=workers;
                options.Families=chosen.Families;options.IncludeApplicationData=chosen.IncludeApplicationData;options.IncludeRootFiles=chosen.IncludeRootFiles;
                options.ScanArchives=chosen.ScanArchives;options.SniffRenamed=chosen.SniffRenamed;options.FullHashScans=chosen.FullHashScans;
                engine.StartScan(request);Message("Scan queued…");
            };
            Message("Creating your catalog…");
        }

        void SelectAll(bool value){foreach(CardItem card in cards)if(card.CanSelect)card.IsSelected=value;UpdateControls();}
        CardItem[] Selected(){if(display==ReviewView.Quick){var card=QuickCard();return card!=null && card.CanSelect ? new[]{card} : new CardItem[0];}return cards.Where(c=>c.IsSelected && c.CanSelect).ToArray();}
        void SetFit(){var style=new Style(typeof(Image));style.Setters.Add(new Setter(Image.StretchProperty,Tag("FitBox")=="Uniform" ? Stretch.Uniform : Stretch.UniformToFill));style.Setters.Add(new Setter(Image.HorizontalAlignmentProperty,HorizontalAlignment.Center));style.Setters.Add(new Setter(Image.VerticalAlignmentProperty,VerticalAlignment.Center));Window.Resources["ThumbnailStyle"]=style;}
        void ResetView(){Box("SearchBox").Clear();Box("FolderBox").Clear();columnSort=null;Combo("TypeBox").SelectedIndex=0;Combo("SmartFilterBox").SelectedIndex=0;Combo("SortBox").SelectedIndex=0;Combo("FitBox").SelectedIndex=0;UpdateColumnHeaders();NewPage(1);}
        void GoToPage(){
            var box=Box("PageBox");int requested;
            if(!Int32.TryParse(box.Text,out requested) || requested<1 || requested>pageCount){
                // Keep the entry so it can be corrected, with the cursor back in the box.
                box.Focus();box.SelectAll();
                throw new ArgumentException("Enter a page from 1 to "+pageCount+".");
            }
            NewPage(requested);
            pageBoxShown=box.Text;ShowPageNumber();
            if(box.IsKeyboardFocused)box.SelectAll();
        }
        // The list refreshes about once a second while a scan runs. A refresh may update the page box only while it
        // still shows the number the app last put there: a number the reviewer is typing must be left alone.
        void ShowPageNumber(){
            var box=Box("PageBox");
            if(pageBoxEditing && box.Text!=pageBoxShown)return;
            string current=page.ToString(CultureInfo.InvariantCulture);
            pageBoxShown=current;
            if(box.Text==current)return;
            box.Text=current;
            if(box.IsKeyboardFocused)box.SelectAll();
        }
        void EndPageEdit(){pageBoxEditing=false;ShowPageNumber();}
        internal int CurrentPage {get{return page;}}
        void NewPage(int target){
            if(!Ready)return;
            pageSearch=Box("SearchBox").Text;pageFolder=Box("FolderBox").Text;
            page=Math.Max(1,target);quickIndex=0;view.Advance();refresh=true;
            ClearCards();Get<ScrollViewer>("GalleryScroll").ScrollToTop();
        }

        void ReadPage() {
            var request=new PageRequest {Generation=view.Current,Bucket=BucketKeys[bucket],Page=page,PageSize=Number("PageSizeBox"),
                Search=pageSearch,Folder=pageFolder,Type=Tag("TypeBox"),Filter=Tag("SmartFilterBox"),Sort=columnSort??Tag("SortBox"),SortDescending=columnSort==null ? (bool?)null : columnDescending,GemmaAvailable=gemmaAvailable,
                CachedPreviews=cards.Where(c=>c.Image!=null || c.Record.PreviewBytes!=null)
                    .ToDictionary(c=>c.Record.GroupId,c=>new PreviewVersion(c.Record))};
            long commitVersion=pageCommitVersion;
            pageRequest=engine.RequestPage(request);refresh=false;
            Queue(pageRequest,r=>{
                pageRequest=null;
                if(!view.Accept(r.Generation) || commitVersion!=pageCommitVersion)return;
                if(r.Success)ApplyPage(r.Page);else Message(r.Error);
            });
        }

        internal void ApplyPage(PageData result) {
            bool listWasEmpty=cards.Count==0;
            revision=result.Revision;page=result.Page;
            pageCount=(int)Math.Max(1,Math.Min(Int32.MaxValue,(result.Total+result.PageSize-1)/result.PageSize));
            ShowPageNumber();
            Text("PageLabel","Page "+page+" of "+pageCount+" · "+result.Total.ToString("N0")+" matching items");
            marqoThreshold=result.MarqoThreshold;gemmaThreshold=result.GemmaThreshold;useGemma=result.UseGemma;gemmaCpuOnly=result.GemmaCpuOnly;gemmaCriteria=result.GemmaCriteria;gemmaSettingsLoaded=true;
            Button("Queue4").ToolTip="Suggestions from "+(useGemma ? "Gemma 4 E2B" : "Marqo")+", highest score first by default.";
            for(int i=0;i<BucketNames.Length;i++)Button("Queue"+i).Content=((ComboBoxItem)Combo("ViewBox").Items[i]).Content=BucketIcons[i]+BucketNames[i]+" ("+(i==4 ? result.AiTotal : result.Totals[i]).ToString("N0")+")";
            var active=display==ReviewView.Quick ? QuickCard() : null;
            MediaRow activeRow=active==null ? null : active.Record;
            int previousPosition=active==null ? quickIndex : QuickPosition;
            var ids=new HashSet<long>(result.Rows.Select(row=>row.GroupId));
            var remaining=cards.Where(card=>ids.Contains(card.Record.GroupId)).ToDictionary(card=>card.Record.GroupId);
            for(int i=cards.Count-1;i>=0;i--){
                if(ids.Contains(cards[i].Record.GroupId))continue;
                cards[i].PropertyChanged-=CardChanged;
                cards.RemoveAt(i);
            }
            int activePosition=-1;
            for(int i=0;i<result.Rows.Count;i++){
                MediaRow row=result.Rows[i];
                CardItem card;
                if(!remaining.TryGetValue(row.GroupId,out card)){
                    card=new CardItem(row);card.PropertyChanged+=CardChanged;cards.Insert(i,card);
                }else{
                    card.Update(row);
                    if(!ReferenceEquals(cards[i],card))cards.Move(cards.IndexOf(card),i);
                }
                if(activeRow!=null && activeRow.GroupId==row.GroupId && activeRow.ContentId==row.ContentId)activePosition=i;
            }
            // If the current item leaves the page, keep its slot or fall back to the last slot.
            quickIndex=activePosition>=0 ? activePosition : Math.Min(previousPosition,Math.Max(0,cards.Count-1));
            if(activeRow!=null && (activePosition<0 || !SamePreviewSource(activeRow,cards[activePosition].Record))){
                CancelQuickGesture();
                ResetQuickPreview();
            }
            Get<Border>("EmptyPanel").Visibility=cards.Count==0 ? Visibility.Visible : Visibility.Collapsed;
            bool filtered=!String.IsNullOrWhiteSpace(pageSearch) || !String.IsNullOrWhiteSpace(pageFolder) || !String.IsNullOrEmpty(Tag("TypeBox")) || !String.IsNullOrEmpty(Tag("SmartFilterBox"));
            Text("EmptyTitle",bucket==3 && filtered ? "🔒 Evidence details require unlock" : "🎉 All clear in this view!");
            Text("EmptyText",bucket==3 && filtered ? "Clear search and filters to browse Evidence items. Unlock a saved original to view its authenticated capture details." : "Nothing matches right now — try scanning a folder, switching spaces on the left, or clearing your filters.");
            if(bucket==4 && cards.Count==0){Text("EmptyTitle","No matching AI suggestions");Text("EmptyText","Run an AI draft check on your catalog, adjust the threshold, or clear the active filters.");}
            // A freshly filled list starts with its top item highlighted, so the first decision needs no extra click.
            // Only then: a list that was already showing keeps whatever the reviewer selected or cleared.
            if(listWasEmpty && display!=ReviewView.Quick && !cards.Any(c=>c.IsSelected))SelectCardAt(0);
        }

        // Highlights the selectable item at or after the given position (or the last one before it) and nothing else.
        void SelectCardAt(int index) {
            if(cards.Count==0)return;
            index=Math.Max(0,Math.Min(index,cards.Count-1));
            CardItem target=cards.Skip(index).FirstOrDefault(c=>c.CanSelect)??cards.Take(index).LastOrDefault(c=>c.CanSelect);
            if(target==null)return;
            foreach(CardItem card in cards)card.IsSelected=ReferenceEquals(card,target);
            if(display==ReviewView.List)Get<ListView>("ReviewList").ScrollIntoView(target);
            else{var element=Get<ItemsControl>("CardList").ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;if(element!=null)element.BringIntoView();}
            UpdateControls();
        }
        internal string[] CardNames {get{return cards.Select(c=>c.Record.Name).ToArray();}}
        internal string[] SelectedCardNames {get{return cards.Where(c=>c.IsSelected).Select(c=>c.Record.Name).ToArray();}}

        void ClearCards(){CancelQuickGesture();ResetQuickPreview();foreach(var card in cards)card.PropertyChanged-=CardChanged;cards.Clear();}
        void Remove(CardItem card){
            int index=cards.IndexOf(card);
            if(index<0)return;
            if(display==ReviewView.Quick){
                int current=QuickPosition;
                if(index==current){CancelQuickGesture();ResetQuickPreview();}
                if(index<current)quickIndex=current-1;
            }
            card.PropertyChanged-=CardChanged;cards.RemoveAt(index);
        }
        void CardChanged(object sender,PropertyChangedEventArgs e){if((e.PropertyName=="IsSelected" || e.PropertyName=="CanSelect") && !selection.Updating)UpdateControls();}
        void CardClick(object sender,RoutedEventArgs e) {
            var button=e.OriginalSource as Button;var card=button==null ? null : button.DataContext as CardItem;
            if(card==null || !Ready)return;
            if(button.Name=="CardDetailsButton")Guard(()=>Details(card.Record));
            if(button.Name=="CardInspectButton")Guard(()=>Inspect(card.Record));
        }

        void Review(string destination,CardItem[] chosen,Action<UiResult> done=null,string reviewer=null,string notes=null,Func<bool> stillIntended=null) {
            if(chosen.Length==0 || stillIntended!=null && !stillIntended())return;
            bool protectedChange=destination=="NON_COMPLIANT" || destination=="EVIDENCE" || chosen.Any(c=>c.Record.Status=="EVIDENCE");
            if(protectedChange && engine.DraftReviewActive)throw new InvalidOperationException("Finish or cancel the AI draft check before saving a review that uses protected originals.");
            if(protectedChange && !engine.VaultUnlocked){
                var catalog=engine;
                long generationAtPrompt=view.Current;
                var originalRows=chosen.Select(c=>c.Record).ToArray();
                string label=reviewer??Box("ReviewerBox").Text, draft=notes??Box("NotesBox").Text;
                foreach(var card in chosen)card.IsPending=true;
                Action release=()=>{foreach(var card in chosen)card.IsPending=false;};
                Unlock(()=>{
                    release();
                    if(!Ready || catalog!=engine || !view.Accept(generationAtPrompt) || stillIntended!=null && !stillIntended() || chosen.Where((card,i)=>!card.CanSelect || card.Record.ContentId!=originalRows[i].ContentId || card.Record.ReviewRevision!=originalRows[i].ReviewRevision).Any()){
                        Message("The selection or saved review changed while unlocking. Select the item again.");return;
                    }
                    Review(destination,chosen,done,label,draft,stillIntended);
                },release);return;
            }
            var undoSnapshots=chosen.Select(c=>new ReviewUndoItem {
                GroupId=c.Record.GroupId,PriorRevision=c.Record.ReviewRevision,Status=c.Record.Status??"TO_REVIEW",
                Reviewer=c.Record.Reviewer??"",Notes=c.Record.Notes??""
            }).ToArray();
            var request=new ReviewRequest {Status=destination,OperationId=Id(),Reviewer=reviewer??Box("ReviewerBox").Text,Notes=notes??Box("NotesBox").Text,
                GroupIds=chosen.Select(c=>c.Record.GroupId).ToArray(),ExpectedRevisions=chosen.ToDictionary(c=>c.Record.GroupId,c=>c.Record.ReviewRevision)};
            bool fromEvidence=chosen.Any(c=>c.Record.Status=="EVIDENCE");
            if(destination=="EVIDENCE" && fromEvidence)throw new InvalidOperationException("The selected item is already in Evidence.");
            if(fromEvidence && destination!="EVIDENCE" && MessageBox.Show(Window,"Move the selected items out of Evidence?","Move Evidence",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            string id=fromEvidence ? engine.RequestEvidenceReclassify(request) : destination=="EVIDENCE" ? engine.RequestOriginalRetention(request) : engine.RequestReview(request);
            if(protectedChange)task=id;
            foreach(CardItem card in chosen)card.IsPending=true;
            long generation=view.Current;
            Queue(id,r=>{
                if(task==id)task=null;
                if(r.Success)pageCommitVersion++;
                foreach(CardItem card in chosen){card.IsPending=false;if(r.Success){card.IsSelected=false;if(view.Accept(generation) && destination!=BucketKeys[bucket])Remove(card);}}
                Message(r.Success ? "Review saved." : "Review failed. Selection retained. "+r.Error);
                if(destination=="EVIDENCE" && r.GroupIds!=null)
                    foreach(var window in ordinaryDetails.Where(pair=>r.GroupIds.Contains(pair.Value)).Select(pair=>pair.Key).ToArray())
                        window.Close();
                if(r.Success){
                    if(!fromEvidence && destination!="EVIDENCE")RememberUndo(undoSnapshots,r.ReviewRevisions);
                    if(destination=="NON_COMPLIANT" && Get<CheckBox>("AutoClearNotesBox").IsChecked==true && notes==null)Box("NotesBox").Clear();
                    refresh=true;
                }
                if(done!=null)done(r);
            });
            Message("Review pending…");
        }

        internal static void AppendNoteTag(TextBox box,string tag) {
            if(box==null || String.IsNullOrWhiteSpace(tag) || box.IsReadOnly)return;
            string current=(box.Text??"").Trim();
            var parts=current.Length==0 ? new List<string>() : current.Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).Where(s=>s.Length>0).ToList();
            if(parts.Any(p=>String.Equals(p,tag,StringComparison.OrdinalIgnoreCase)))return;
            parts.Add(tag);
            string updated=String.Join("; ",parts);
            if(updated.Length<=box.MaxLength || box.MaxLength<=0){box.Text=updated;box.CaretIndex=box.Text.Length;}
        }

        void RememberUndo(ReviewUndoItem[] items,Dictionary<long,long> revisions) {
            long cost=2048+items.Sum(item=>item.Cost);
            if(revisions==null || cost>ReviewerEngine.UndoQueueLimit || items.Any(item=>
                !revisions.ContainsKey(item.GroupId) || item.Notes.Length>16384 || item.Reviewer.Length>128))return;
            foreach(var item in items)item.ExpectedRevision=revisions[item.GroupId];
            while(undoStack.Count>=32 || undoStack.Sum(entry=>entry.Cost)+cost>4L*1024*1024)
                undoStack.RemoveAt(0);
            undoStack.Add(new UndoEntry {Items=items,OperationId=Id(),Cost=cost});
        }

        void UndoLastReview() {
            if(!Ready || undoInFlight || task!=null || undoStack.Count==0)return;
            var entry=undoStack[undoStack.Count-1];
            string request=engine.RequestUndoReview(entry.Items,entry.OperationId);
            undoInFlight=true;
            Message("Undo pending…");
            Queue(request,r=>{
                undoInFlight=false;
                if(r.Success){
                    pageCommitVersion++;
                    int index=undoStack.IndexOf(entry);
                    // Carry the revision back only if Undo restored the state written by the earlier review.
                    if(r.ReviewRevisions!=null)
                        foreach(var restored in entry.Items){
                            var predecessor=undoStack.Take(Math.Max(0,index)).Reverse().SelectMany(e=>e.Items)
                                .FirstOrDefault(item=>item.GroupId==restored.GroupId);
                            if(predecessor!=null && predecessor.ExpectedRevision==restored.PriorRevision)
                                predecessor.ExpectedRevision=r.ReviewRevisions[restored.GroupId];
                        }
                    undoStack.Remove(entry);
                    refresh=true;
                }
                // The item was edited after this review (for example its notes were saved). While the step is still on
                // top, nothing can make it apply again: only undoing a later step restores an earlier revision. Drop it,
                // otherwise every later Ctrl+Z retries it and older steps are unreachable. If a newer review was saved
                // while this Undo was in flight, the step is no longer on top and undoing that review re-arms it, so keep it.
                bool stale=!r.Success && r.ReviewChanged && undoStack.Count>0 && ReferenceEquals(undoStack[undoStack.Count-1],entry);
                if(stale){
                    undoStack.Remove(entry);
                    refresh=true;
                }
                Message(r.Success ? "Review undone." : stale
                    ? "That review can no longer be undone because the item was changed afterwards (for example its notes were edited). Nothing was changed."+
                        (undoStack.Count>0 ? " Earlier reviews can still be undone." : "")
                    : "Undo failed. "+r.Error);
                UpdateControls();
            });
            UpdateControls();
        }

        void Tick() {
            if(closing){ShutdownTick();return;}
            if(engine==null)return;
            status=engine.Status;
            Text("ActivityText",status.RunningScan ? String.Format("{0} · user {1}/{2} · {3:N0}/{4:N0} files processed · {5:N0} errors · {6:N0} queued · WAL {7:N1} MiB",
                status.Stage,status.CompletedUsers,status.TotalUsers,status.ProcessedFiles,status.DiscoveredFiles,status.FailedFiles,status.QueueCount,status.WalBytes/1048576.0)+
                (status.NotFollowed>0 ? " · "+ScanRunner.LinksNotFollowed(status.NotFollowed) : "") : status.Stage);
            if(engine.Faulted && engine.DetectedHtml && pending.Count==0){OfferImport();return;}
            if(engine.Faulted && !String.IsNullOrEmpty(status.Error))Message(status.Error);
            if(engine.Faulted)pendingStart=null;
            UiResult result;
            for(int i=0;i<8 && engine.TryTakeResult(out result);i++)DispatchResult(result);
            if(Ready && !loadedPreferences){loadedPreferences=true;Guard(LoadPreferences);}
            if(Ready && pageRequest==null && (refresh || status.Revision>revision) && !selection.Dragging)Guard(ReadPage);
            for(int i=0;i<12;i++) {
                MediaLoadResult media;if(!loader.TryTakeResult(out media))break;
                if(media.Kind=="Inspect"){
                    if(inspection.Accept(media.Generation)){if(showInspection!=null)showInspection(media);else ApplyQuickPreview(media);}
                    continue;
                }
                if(!view.Accept(media.Generation))continue;
                CardItem card=cards.FirstOrDefault(c=>c.Record.GroupId==media.GroupId && c.Record.ContentId==media.ContentId && c.Record.PreviewRevision==media.PreviewRevision);
                if(card!=null){card.SetImage(media.Image,media.Error);if(media.Image!=null)card.Record.PreviewBytes=null;}
            }
            var focus=FocusedCard();
            IEnumerable<CardItem> toDecode=display==ReviewView.Thumbnails ? (IEnumerable<CardItem>)cards : focus==null ? new CardItem[0] : new[]{focus};
            foreach(CardItem card in toDecode)if(card.CanLoadImage){if(!loader.QueueThumbnail(card.Record.GroupId,card.Record.ContentId,view.Current,card.Record.PreviewBytes,card.Record.PreviewRevision))break;card.ImageQueued=true;}
            if(Ready){
                if(gemmaSettingsLoaded)engine.ConfigureGemma(gemmaAvailable,gemmaCpuOnly,gemmaCriteria);
                Guard(LoadQuickPreview);
            }
            string activeOperation=draftTask??task;
            var progress=activeOperation==null ? null : engine.GetOperationProgress(activeOperation);
            if(progress!=null)Text("ActivityText",progress.Stage+" · "+progress.Completed.ToString("N0")+(progress.Total.HasValue ? " / "+progress.Total.Value.ToString("N0") : "")+" "+progress.Unit);
            var bar=Get<ProgressBar>("ActivityProgress");
            if(status.RunningScan){
                bar.Visibility=Visibility.Visible;
                if(status.DiscoveredFiles>0){bar.IsIndeterminate=false;bar.Value=Math.Min(100.0,100.0*status.ProcessedFiles/status.DiscoveredFiles);}
                else bar.IsIndeterminate=true;
            }else if(progress!=null){
                bar.Visibility=Visibility.Visible;
                if(progress.Total.HasValue && progress.Total.Value>0){bar.IsIndeterminate=false;bar.Value=Math.Min(100.0,100.0*progress.Completed/progress.Total.Value);}
                else bar.IsIndeterminate=true;
            }else{
                bar.Visibility=Visibility.Collapsed;bar.IsIndeterminate=false;bar.Value=0;
            }
            UpdateControls();
        }

        void DispatchResult(UiResult result) {
            Action<UiResult> action;
            try{
                if(pending.TryGetValue(result.RequestId,out action)){pending.Remove(result.RequestId);action(result);}
                else if(result.Kind=="VideoSource")PlaybackProxy.Release(result.Value);
            }
            catch(Exception e){Message(e.GetBaseException().Message);}
            finally{if(result.Evidence!=null)result.Evidence.Dispose();}
        }

        void UpdateControls() {
            bool ready=Ready,scanning=status!=null && status.RunningScan;
            int count=Selected().Length;
            Text("SelectionLabel",count+" selected");
            Button("CatalogButton").IsEnabled=!closing && !switchingCatalog && closingDatabase==null && !scanning && task==null && pending.Count==0 && (engine==null || engine.Ready || engine.Faulted);
            bool drafting=ready && engine.DraftReviewActive;
            // With no catalog open, Start scan creates the default catalog first.
            Button("StartButton").IsEnabled=(ready && !scanning && !drafting) || (engine==null && !closing && !switchingCatalog && closingDatabase==null);
            Button("ResumeButton").IsEnabled=ready && !scanning && !drafting && status!=null && !String.IsNullOrEmpty(status.ResumeId);
            Button("CancelButton").IsEnabled=ready && scanning;
            Button("AiDraftButton").IsEnabled=ready && revision>=0;
            foreach(string name in new[]{"FilterButton","ResetButton","GoButton","LogButton","MaintenanceButton","PreviewTasksButton","VaultButton"})Button(name).IsEnabled=ready;
            Button("PreviousButton").IsEnabled=ready && page>1;Button("NextButton").IsEnabled=ready && page<pageCount;
            Combo("ViewBox").IsEnabled=ready;
            foreach(string name in new[]{"CompliantButton","NonCompliantButton","UnreviewedButton","RetryPreviewsButton","RetainOriginalsButton","CaptureEvidenceButton","ClearSelectionButton"})Button(name).IsEnabled=ready && count>0;
            foreach(string name in new[]{"VerifyButton","IdentityHistoryButton","StoredOriginalButton","ExportOriginalButton"})Button(name).IsEnabled=ready && count==1;
            Button("SelectPageButton").IsEnabled=ready && display!=ReviewView.Quick && cards.Any(c=>c.CanSelect);
            Button("ClearSelectionButton").IsEnabled=ready && display!=ReviewView.Quick && count>0;
            bool canUndo=ready && !undoInFlight && undoStack.Count>0 && task==null;
            Button("UndoReviewButton").IsEnabled=Button("QuickUndoButton").IsEnabled=canUndo;
            string undoTip=canUndo ? "Undo last review ("+undoStack[undoStack.Count-1].Items.Length+" item"+(undoStack[undoStack.Count-1].Items.Length==1 ? "" : "s")+") (Ctrl+Z)" : "Undo the last review decision in this session (Ctrl+Z)";
            Button("UndoReviewButton").ToolTip=Button("QuickUndoButton").ToolTip=undoTip;
            Button("CancelWorkButton").Visibility=ready && (task!=null || draftTask!=null) ? Visibility.Visible : Visibility.Collapsed;
            Button("VaultButton").Content=ready && engine.VaultUnlocked ? "🔒 Lock originals" : "🔑 Unlock originals";
            UpdateWorkspace();
        }

        void Closing(object sender,CancelEventArgs args) {
            if(allowClose)return;args.Cancel=true;if(closing)return;closing=true;
            SaveWorkspace();UpdateControls();
            if(inspector!=null)inspector.Close();
            if(protectedWindow!=null)protectedWindow.Close();
            CloseDatabaseWindows();
            Get<Border>("ShutdownPanel").Visibility=Visibility.Visible;
            loader.CloseAsync();
            if(switchingCatalog){waitingOnClosePreference=true;Text("ShutdownStage","Waiting for the pending preference save…");}
            else SaveBeforeClose();
        }

        void SaveBeforeClose() {
            Button("ShutdownRetryButton").Visibility=Button("ShutdownKeepSavedButton").Visibility=Visibility.Collapsed;
            Text("ShutdownStage","Saving preferences and closing the database…");
            waitingOnClosePreference=false;
            if(engine==null)return;
            try {
                string save=SavePreferences(engine);
                if(save==null){engine.CloseAsync();return;}
                waitingOnClosePreference=true;
                Queue(save,r=>{if(r.Success){waitingOnClosePreference=false;engine.CloseAsync();}else PreferenceCloseFailed(r.Error);});
            }catch(Exception error){PreferenceCloseFailed(error.GetBaseException().Message);}
        }

        void PreferenceCloseFailed(string error) {
            waitingOnClosePreference=true;
            Text("ShutdownStage","Preferences could not be saved. "+error);
            Button("ShutdownRetryButton").Visibility=Button("ShutdownKeepSavedButton").Visibility=Visibility.Visible;
        }

        void ShutdownTick() {
            UiResult result;
            if(engine!=null)while(engine.TryTakeResult(out result))DispatchResult(result);
            if(closingDatabase!=null || waitingOnClosePreference)return;
            if(engine!=null && !engine.Closed){if(!String.IsNullOrEmpty(engine.CloseStage))Text("ShutdownStage",engine.CloseStage);return;}
            if(!loader.Closed){Text("ShutdownStage","Database closed. Stopping preview workers…");return;}
            if(engine!=null){
                if(!databaseClosedAt.HasValue){databaseClosedAt=DateTime.UtcNow;Text("ShutdownStage","Database closed. Committed work is saved.");Get<ProgressBar>("ShutdownProgress").IsIndeterminate=false;Get<ProgressBar>("ShutdownProgress").Value=100;return;}
                if((DateTime.UtcNow-databaseClosedAt.Value).TotalMilliseconds<550)return;
            }
            timer.Stop();allowClose=true;Window.Close();
        }

        public void Dispose(){timer.Stop();selection.Dispose();if(!loader.Closed)loader.Dispose();if(engine!=null && !engine.Closed)engine.Dispose();}
    }
}
