using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MediaReviewer {
    sealed partial class ReviewerWindow {
        Window protectedWindow;
        bool offeredImport;

        string ChooseFolder(string title,string current) {
            using(var dialog=new System.Windows.Forms.FolderBrowserDialog {Description=title,SelectedPath=current??"",ShowNewFolderButton=false}) {
                var owner=new System.Windows.Interop.WindowInteropHelper(Window).Handle;
                return dialog.ShowDialog(new FormsOwner(owner))==System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }
        sealed class FormsOwner:System.Windows.Forms.IWin32Window {public IntPtr Handle{get;private set;} internal FormsOwner(IntPtr handle){Handle=handle;}}
        string OpenFile(string filter){var picker=new OpenFileDialog {Filter=filter};return picker.ShowDialog(Window)==true ? picker.FileName : null;}
        string SaveFile(string filter,string name){var picker=new SaveFileDialog {Filter=filter,FileName=name,OverwritePrompt=false};return picker.ShowDialog(Window)==true ? picker.FileName : null;}

        Window Dialog(string title,FrameworkElement body,double width=680,double height=480) {
            var dialog=new Window {Title=title,Owner=Window,WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Width=width,Height=height,MinWidth=Math.Min(420,width),MinHeight=Math.Min(240,height),FontFamily=Window.FontFamily,
                FontSize=Window.FontSize,Background=Window.Background,Foreground=Window.Foreground,Content=body};
            dialog.Resources.MergedDictionaries.Add(Window.Resources);
            dialog.SetResourceReference(Control.BackgroundProperty,"PageBrush");
            dialog.SetResourceReference(Control.ForegroundProperty,"TextBrush");
            return dialog;
        }
        static Button ActionButton(string label,Action action) {
            var b=new Button {Content=label,MinHeight=30,Padding=new Thickness(10,4,10,4),Margin=new Thickness(3)};
            b.Click+=(s,e)=>action();return b;
        }
        static TextBlock Label(string text){return new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8)};}
        static WrapPanel Bar(){return new WrapPanel {HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,8,0,0)};}
        static void Dock(DockPanel parent,UIElement child,Dock side){DockPanel.SetDock(child,side);parent.Children.Add(child);}
        static TextBox Editor(string value,int limit=16384){return new TextBox {Text=value??"",MaxLength=limit,Padding=new Thickness(6),VerticalContentAlignment=VerticalAlignment.Center};}
        void TrackDatabaseWindow(Window dialog){databaseWindows.Add(dialog);dialog.Closed+=(s,e)=>databaseWindows.Remove(dialog);}
        void CloseDatabaseWindows(){foreach(Window dialog in databaseWindows.ToArray())dialog.Close();}

        void About() {
            var panel=new DockPanel {Margin=new Thickness(18)};var dialog=Dialog("About Media Compliance Reviewer",panel,720,560);
            var actions=Bar();actions.Children.Add(ActionButton("Close",dialog.Close));Dock(panel,actions,System.Windows.Controls.Dock.Bottom);
            var text=new StackPanel();panel.Children.Add(new ScrollViewer {Content=text,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            text.Children.Add(Label("Media Compliance Reviewer for Windows\n.NET development build · "+Product.DotNetVersion+" · "+Product.DotNetDate+"\nMade by "+Product.Author+", "+Product.Contributors+"\n"+Product.EventCredit));
            using(var resource=typeof(ReviewerWindow).Assembly.GetManifestResourceStream("COPYING.LGPLv2.1"))
            using(var reader=new StreamReader(resource)) {
                text.Children.Add(new Expander {Header="SQLite "+Native.SqliteVersion+" · Public domain",Content=Label("SQLite is statically linked. Source: https://www.sqlite.org/2026/sqlite-amalgamation-3540100.zip")});
                text.Children.Add(new Expander {Header="FFmpeg 9.0.2 · LGPL 2.1 or later",Content=Label("FFmpeg is statically linked. Corresponding source: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz\n\n"+reader.ReadToEnd())});
            }
            using(var resource=typeof(ReviewerWindow).Assembly.GetManifestResourceStream("COPYING.dav1d"))
            using(var reader=new StreamReader(resource))text.Children.Add(new Expander {Header="dav1d 1.5.4 · BSD 2-Clause",Content=Label(reader.ReadToEnd())});
            using(var resource=typeof(ReviewerWindow).Assembly.GetManifestResourceStream("COPYING.draft-ai"))
            using(var reader=new StreamReader(resource))text.Children.Add(new Expander {Header="AI draft check · Marqo and OpenCV",Content=Label("Marqo NSFW Image Detection 384 weights and OpenCV 5.0.0 use the Apache 2.0 license. The model file is an ONNX conversion of Marqo's original checkpoint.\n\n"+reader.ReadToEnd())});
            using(var resource=typeof(ReviewerWindow).Assembly.GetManifestResourceStream("COPYING.gemma-runtime"))
            using(var reader=new StreamReader(resource))text.Children.Add(new Expander {Header="Gemma 4 E2B and llama.cpp",Content=Label("Google Gemma 4 weights use Apache 2.0. The linked llama.cpp runtime uses MIT.\n\n"+reader.ReadToEnd())});
            text.Children.Add(new Expander {Header="Microsoft Windows components",Content=Label("WPF, Windows Imaging Component and .NET Framework are supplied by Windows.")});
            dialog.Show();
        }
        void ShowText(string title,string value) {
            var content=new DockPanel {Margin=new Thickness(16)};var dialog=Dialog(title,content);
            var actions=Bar();Dock(content,actions,System.Windows.Controls.Dock.Bottom);
            if(title=="Log")actions.Children.Add(ActionButton("Save text…",()=>Guard(()=>{string path=SaveFile("Text file (*.txt)|*.txt","Media Review Log.txt");if(path!=null)using(var stream=new FileStream(path,FileMode.CreateNew))using(var writer=new StreamWriter(stream))writer.Write(value);})));
            actions.Children.Add(ActionButton("Close",dialog.Close));
            content.Children.Add(new TextBox {Text=value??"",IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(6)});
            dialog.Show();
        }

        void OfferImport() {
            if(offeredImport)return;offeredImport=true;
            string source=engine.DatabasePath;
            if(MessageBox.Show(Window,"A final HTML database was detected. Create a Windows copy for reviewing?","Open HTML database",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            string destination=SaveFile("SQLite database (*.sqlite)|*.sqlite",Path.GetFileNameWithoutExtension(source)+"-Windows.sqlite");
            if(destination==null)return;
            var old=engine;engine=null;closingDatabase=old;old.CloseAsync();
            var wait=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
            wait.Tick+=(s,e)=>{if(old.Closed){wait.Stop();closingDatabase=null;if(!closing)Guard(()=>StartOpen(destination,true,source));}};wait.Start();
        }

        void ChooseOptions(){
            var panel=new StackPanel {Margin=new Thickness(16)};var dialog=Dialog("Scan options",panel,550,355);
            var appData=new CheckBox {Content="Include Application Data and AppData",IsChecked=options.IncludeApplicationData,Margin=new Thickness(4)};
            var root=new CheckBox {Content="Include root-level media files",IsChecked=options.IncludeRootFiles,Margin=new Thickness(4)};
            var zip=new CheckBox {Content="Scan supported ZIP archives",IsChecked=options.ScanArchives,Margin=new Thickness(4)};
            var sniff=new CheckBox {Content="Detect renamed media by file header",IsChecked=options.SniffRenamed,Margin=new Thickness(4),ToolTip="Checks a small header on ordinary files with unknown extensions. It can increase reads on a network share."};
            var full=new CheckBox {Content="Read every selected file for full SHA-256",IsChecked=options.FullHashScans,Margin=new Thickness(4)};
            panel.Children.Add(Label("Choose which files to scan and how to verify them. Deep always includes AppData."));
            foreach(var toggle in new[]{appData,root,zip,sniff,full})panel.Children.Add(toggle);
            var actions=Bar();panel.Children.Add(actions);
            actions.Children.Add(ActionButton("Apply",()=>{options.IncludeApplicationData=appData.IsChecked==true;options.IncludeRootFiles=root.IsChecked==true;options.ScanArchives=zip.IsChecked==true;options.SniffRenamed=sniff.IsChecked==true;options.FullHashScans=full.IsChecked==true;dialog.Close();}));
            actions.Children.Add(ActionButton("Cancel",dialog.Close));dialog.ShowDialog();
        }

        void ChooseFormats(){
            var content=new DockPanel {Margin=new Thickness(16)};var dialog=Dialog("File types",content,530,520);var actions=Bar();
            Dock(content,actions,System.Windows.Controls.Dock.Bottom);
            var list=new StackPanel();
            var checks=Formats.Families.Select(f=>new CheckBox {Content=f.Label+"  ("+String.Join(", ",f.Extensions)+")",Tag=f.Key,
                IsChecked=options.Families.Contains(f.Key),Margin=new Thickness(3,4,3,4)}).ToArray();
            foreach(var check in checks)list.Children.Add(check);
            actions.Children.Add(ActionButton("Apply",()=>{var families=checks.Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToArray();if(families.Length==0)return;options.Families=families;Combo("ModeBox").SelectedIndex=2;dialog.Close();}));
            actions.Children.Add(ActionButton("Cancel",dialog.Close));content.Children.Add(new ScrollViewer {Content=list});dialog.ShowDialog();
        }

        // Read the PasswordBox through its SecureString so no managed string copy of the password is created;
        // a string could not be cleared and would stay in memory until the collector happened to reclaim it.
        static byte[] PasswordBytes(System.Security.SecureString secure){
            if(secure==null || secure.Length==0)return new byte[0];
            IntPtr text=System.Runtime.InteropServices.Marshal.SecureStringToBSTR(secure);
            try{
                char[] chars=new char[secure.Length];
                try{System.Runtime.InteropServices.Marshal.Copy(text,chars,0,chars.Length);return Encoding.UTF8.GetBytes(chars);}
                finally{Array.Clear(chars,0,chars.Length);}
            }finally{System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(text);}
        }

        byte[] Password(string title,string prompt,bool confirm){
            var panel=new StackPanel {Margin=new Thickness(18)};var dialog=Dialog(title,panel,535,confirm ? 315 : 250);
            panel.Children.Add(Label(prompt));
            var input=new PasswordBox {Height=36,Padding=new Thickness(6),VerticalContentAlignment=VerticalAlignment.Center,MaxLength=16384};panel.Children.Add(input);
            var again=new PasswordBox {Height=36,Padding=new Thickness(6),VerticalContentAlignment=VerticalAlignment.Center,MaxLength=16384};
            if(confirm){panel.Children.Add(Label("Confirm password"));panel.Children.Add(again);}
            var actions=Bar();panel.Children.Add(actions);byte[] value=null;
            var ok=ActionButton(confirm ? "Set password" : "Unlock",()=>{
                byte[] first=null,second=null;try{using(var secure=input.SecurePassword)first=PasswordBytes(secure);if(confirm)using(var secure=again.SecurePassword)second=PasswordBytes(secure);if(first.Length==0 || confirm && !first.SequenceEqual(second))return;
                value=first;first=null;dialog.DialogResult=true;}finally{if(first!=null)Array.Clear(first,0,first.Length);if(second!=null)Array.Clear(second,0,second.Length);}
            });
            ok.IsDefault=true;actions.Children.Add(ok);var cancel=ActionButton("Cancel",dialog.Close);cancel.IsCancel=true;actions.Children.Add(cancel);
            dialog.Loaded+=(s,e)=>input.Focus();try{dialog.ShowDialog();return value;}finally{input.Clear();again.Clear();}
        }

        void Unlock(Action complete,Action abandoned=null) {
            byte[] password=null;
            try {
                bool create=!engine.VaultExists;
                password=Password(create ? "Set originals password" : "Unlock stored originals",
                    create ? "Choose a password for encrypted originals. Keep it somewhere safe." : "Enter the current originals password. Imported HTML Evidence initially uses its saved HTML password.",create);
                if(password==null){if(abandoned!=null)abandoned();return;}
                Work(engine.RequestVaultUnlock(password,create),"Unlock originals",r=>{
                    if(r.Success){if(complete!=null)complete();}
                    else if(abandoned!=null)abandoned();
                },true);
            }catch{
                if(password!=null)Array.Clear(password,0,password.Length);
                if(abandoned!=null)abandoned();
                throw;
            }
        }

        void RetainOriginals() {
            var selected=Selected().Select(c=>c.Record).ToArray();
            if(selected.Length==0 || selected.Any(r=>r.Status!="NON_COMPLIANT"))
                throw new InvalidOperationException("Select Non-compliant items to retain originals without changing their saved decisions.");
            var catalog=engine;
            var ids=selected.Select(r=>r.GroupId).ToArray();
            var revisions=selected.ToDictionary(r=>r.GroupId,r=>r.ReviewRevision);
            string operation=Id();
            Action retain=()=>{
                if(!Ready || engine!=catalog)return;
                Work(catalog.RequestRetainOriginals(ids,revisions,operation),"Retain originals");
            };
            if(catalog.VaultUnlocked)retain();else Unlock(retain);
        }

        void Original(MediaRow row) {
            if(!engine.VaultUnlocked){Unlock(()=>Original(row));return;}
            Work(engine.RequestEvidenceInspect(row.GroupId),"Verify stored original",r=>{
                if(protectedWindow!=null)protectedWindow.Close();
                var content=new DockPanel {Margin=new Thickness(16)};
                var dialog=Dialog("Stored original",content,740,560);protectedWindow=dialog;
                dialog.Closed+=(s,e)=>{content.Children.Clear();if(protectedWindow==dialog)protectedWindow=null;};
                var metadata=Editor(EvidencePackageSession.Describe(r.Evidence));metadata.IsReadOnly=true;metadata.TextWrapping=TextWrapping.Wrap;
                metadata.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;metadata.MaxHeight=180;
                Dock(content,metadata,System.Windows.Controls.Dock.Top);
                var actions=Bar();Dock(content,actions,System.Windows.Controls.Dock.Bottom);
                actions.Children.Add(ActionButton("Export original…",()=>Guard(()=>ExportOriginal(row))));actions.Children.Add(ActionButton("Close",dialog.Close));
                if(r.Evidence.PreviewImage!=null)content.Children.Add(new Image {Source=r.Evidence.PreviewImage,Stretch=Stretch.Uniform});
                else content.Children.Add(Label(r.Evidence.PreviewError??"No stored preview available."));
                dialog.Show();
            });
        }
        void ExportOriginal(MediaRow row) {
            if(!engine.VaultUnlocked){Unlock(()=>ExportOriginal(row));return;}
            string path=SaveFile("All files (*.*)|*.*",row.Name);if(path!=null)Work(engine.RequestEvidenceOriginalExport(row.GroupId,path,Id()),"Export verified original");
        }
        void Verify(MediaRow row){
            Work(engine.RequestFullVerification(row.GroupId,null),"Verify full",r=>{
                var preview=r.IdentityPreview;if(preview==null)return;
                IdentityResults(preview);
            });
        }

        void PopulateMaintenanceItems(ItemsControl menu) {
            Menu(menu,"💾 Portable backup…",()=>{string path=SaveFile("SQLite database (*.sqlite)|*.sqlite","Media Review Backup.sqlite");if(path!=null)Work(engine.RequestBackup(path),"Backup");});
            Menu(menu,"✓ Check database integrity",()=>Work(engine.RequestIntegrity(),"Integrity check"));
            Menu(menu,"🧹 Compact and clean up staging data",()=>Work(engine.RequestDatabaseCleanup(),"Catalog cleanup"));
            Menu(menu,"📏 Retention limits…",()=>Work(engine.RequestOriginalLimits(),"Read retention limits",r=>RetentionLimits(EvidenceFormat.Json().Deserialize<OriginalStorageLimits>(r.Value))));
            Menu(menu,"🔑 Change originals password…",ChangePassword);
            Menu(menu,"📌 Use current source path as default",()=>ChangeDefaultSource(Box("SourceBox").Text.Trim()));
            Menu(menu,"✖ Clear default source path",()=>ChangeDefaultSource(""));
            Menu(menu,"🔗 Sources / reconnect…",ManageSources);
            Menu(menu,"📁 Open catalog folder",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(engine.DatabasePath)){UseShellExecute=true}));
            Menu(menu,"🔄 Reconnect all sources…",()=>{
                bool acceptCandidates;
                string path=PromptSource(out acceptCandidates);
                if(path!=null)Work(engine.RequestReconnectAll(path,acceptCandidates),"Reconnect all sources");
            });
            Menu(menu,"▶ Resume source reconnection",()=>Queue(engine.RequestReconnectAllStatus(),r=>{
                if(!r.Success){Message(r.Error);return;}
                if(String.IsNullOrEmpty(r.Reconnection.Id)){Message("No saved reconnection.");return;}
                // The acceptance is asked again on resume: it is a decision for the person in front of the screen and is not saved in the catalog.
                bool acceptCandidates=false;
                if(r.Reconnection.State=="running" || r.Reconnection.State=="interrupted"){
                    var answer=MessageBox.Show(Window,"Also accept quick candidate matches for the sources still to be checked?\n\nA quick candidate is a larger file matched on sampled parts only. It is not full-file proof.\n\nYes: bind full matches and quick candidates.\nNo: bind full matches only and leave quick candidates unverified.","Resume source reconnection",MessageBoxButton.YesNoCancel,MessageBoxImage.Question,MessageBoxResult.No);
                    if(answer==MessageBoxResult.Cancel)return;
                    acceptCandidates=answer==MessageBoxResult.Yes;
                }
                Work(engine.RequestResumeReconnectAll(r.Reconnection.Id,acceptCandidates),"Resume reconnection");
            }));
            Menu(menu,"🗑 Discard unfinished source reconnection",()=>Queue(engine.RequestReconnectAllStatus(),r=>{
                if(!r.Success){Message(r.Error);return;}
                if(String.IsNullOrEmpty(r.Reconnection.Id)){Message("No saved reconnection.");return;}
                if(MessageBox.Show(Window,"Discard the unfinished source checks? Committed connections are preserved.","Discard source checks",MessageBoxButton.YesNo)==MessageBoxResult.Yes)
                    Work(engine.RequestDiscardReconnectAll(r.Reconnection.Id),"Discard source checks");
            }));
            Menu(menu,"📊 Export review report…",Report);
            Menu(menu,"📦 Export selected protected Evidence package…",()=>{
                if(!engine.VaultUnlocked){Unlock(ExportPackage);return;}ExportPackage();
            });
            Menu(menu,"🧹 Clean up unfinished originals",()=>{if(MessageBox.Show(Window,"Discard hidden unfinished original captures?","Clean up",MessageBoxButton.YesNo)==MessageBoxResult.Yes)Work(engine.RequestOriginalCleanup(),"Clean up originals");});
        }

        ContextMenu CreateThemedMenu() {
            var menu=new ContextMenu();
            menu.Resources.MergedDictionaries.Add(Window.Resources);
            var style=Window.Resources[typeof(ContextMenu)] as Style;
            if(style!=null)menu.Style=style;
            return menu;
        }

        void Maintenance() {
            var menu=CreateThemedMenu();
            PopulateMaintenanceItems(menu);
            menu.PlacementTarget=Button("MaintenanceButton");menu.IsOpen=true;
        }

        internal void ShowAppearanceMenu(Button target) {
            var menu=CreateThemedMenu();
            string current=CurrentThemeId();
            foreach(var theme in Themes){
                var def=theme;
                var item=new MenuItem {Header=def.Label+(def.Id==current ? "  (Active)" : "")};
                item.Click+=(s,e)=>Guard(()=>SetTheme(def.Id));
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            Menu(menu,"↻ Cycle to next color mode",CycleTheme);
            menu.PlacementTarget=target;menu.IsOpen=true;
        }

        void PromptAgeFilter() {
            var body=new StackPanel {Margin=new Thickness(18)};var dialog=Dialog("Skip older files",body,430,240);
            body.Children.Add(Label("Skip files older than how many years? (Enter 0 to include all files regardless of age.)"));
            var box=Editor(Box("AgeBox").Text,8);body.Children.Add(box);
            var actions=Bar();body.Children.Add(actions);
            var apply=ActionButton("Save",()=>{
                int n;if(!Int32.TryParse(box.Text.Trim(),out n) || n<0 || n>ScanOptions.MaxModifiedAgeYears)return;
                Box("AgeBox").Text=n.ToString(CultureInfo.InvariantCulture);dialog.DialogResult=true;
            });
            apply.IsDefault=true;actions.Children.Add(apply);
            actions.Children.Add(ActionButton("Cancel",dialog.Close));
            dialog.ShowDialog();
        }

        string EditGemmaCriteria(Window owner,string criteria){
            var body=new DockPanel {Margin=new Thickness(18)};
            var dialog=Dialog("Gemma search criteria",body,690,515);
            dialog.Owner=owner;
            var guidance=Label("Describe what Gemma should look for in saved previews. Keep the five category labels A through E. Their scores are A 97%, B 90%, C 55%, D 8%, and E 82%. The suggestion threshold determines which categories appear for review.");
            Dock(body,guidance,System.Windows.Controls.Dock.Top);
            var editor=Editor(String.IsNullOrEmpty(criteria) ? GemmaCriteria.Default : criteria,GemmaCriteria.Limit);
            editor.AcceptsReturn=true;editor.TextWrapping=TextWrapping.Wrap;
            editor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;editor.VerticalContentAlignment=VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(editor,"Gemma search criteria");
            var bottom=new StackPanel();
            Dock(body,bottom,System.Windows.Controls.Dock.Bottom);
            var feedback=Label("Saving a changed rubric requires a new Gemma draft check. Human decisions are kept.");
            feedback.MinHeight=36;bottom.Children.Add(feedback);
            var actions=Bar();bottom.Children.Add(actions);
            actions.Children.Add(ActionButton("Reset to default",()=>{editor.Text=GemmaCriteria.Default;editor.Focus();}));
            string edited=criteria;
            actions.Children.Add(ActionButton("Apply",()=>{
                try{edited=GemmaCriteria.Normalize(editor.Text);dialog.DialogResult=true;}
                catch(ArgumentException error){feedback.Text=error.Message;editor.Focus();}
            }));
            var cancel=ActionButton("Cancel",dialog.Close);cancel.IsCancel=true;actions.Children.Add(cancel);
            body.Children.Add(editor);
            dialog.Loaded+=(s,e)=>editor.Focus();
            dialog.ShowDialog();
            return edited;
        }

        void ShowDraftReview(){
            if(!Ready)return;
            bool available=DraftModelWorker.GemmaAvailable();
            if(available!=gemmaAvailable){gemmaAvailable=available;InvalidateView();}
            var body=new StackPanel {Margin=new Thickness(18)};
            var dialog=Dialog("AI draft check",body,590,510);
            body.Children.Add(Label("Choose a model for saved images and sampled frames from videos and GIFs. Matches appear in AI suggestions for human confirmation."));
            var model=new ComboBox();model.Items.Add("Marqo · fast image screening");
            if(available)model.Items.Add("Gemma 4 E2B · workplace policy");
            model.SelectedIndex=available && useGemma ? 1 : 0;
            System.Windows.Automation.AutomationProperties.SetName(model,"AI review model");body.Children.Add(model);
            if(!available)body.Children.Add(Label(DraftModelWorker.GemmaCpuSupported() ?
                "Place both Gemma GGUF files beside FleetHacker to enable the Gemma option." :
                "Gemma requires AVX2, FMA and F16C on this device."));
            var devicePanel=new StackPanel();devicePanel.Children.Add(Label("Gemma processing device"));
            var device=new ComboBox();device.Items.Add("Auto (use GPU when available)");device.Items.Add("CPU only");device.SelectedIndex=gemmaCpuOnly ? 1 : 0;
            System.Windows.Automation.AutomationProperties.SetName(device,"Gemma processing device");devicePanel.Children.Add(device);body.Children.Add(devicePanel);
            string savedCriteria=gemmaCriteria,criteria=savedCriteria;
            Action updateCriteriaGuidance=()=>{};
            var criteriaButtonLabel=Label(criteria=="" ? "Default rubric" : "Custom rubric");
            var criteriaButton=ActionButton("Edit Gemma search criteria…",()=>{
                criteria=EditGemmaCriteria(dialog,criteria);
                criteriaButtonLabel.Text=criteria=="" ? "Default rubric" : "Custom rubric";
                updateCriteriaGuidance();
            });
            criteriaButton.HorizontalAlignment=HorizontalAlignment.Left;
            criteriaButton.IsEnabled=!engine.DraftReviewActive;
            criteriaButton.ToolTip=engine.DraftReviewActive ? "Finish or stop the draft check before editing the criteria." : "Change what Gemma looks for in saved previews.";
            var criteriaActions=new WrapPanel();criteriaActions.Children.Add(criteriaButton);criteriaActions.Children.Add(criteriaButtonLabel);devicePanel.Children.Add(criteriaActions);
            if(available){
                var catalog=engine;
                var loadStatus=Label(catalog.GemmaStatus);devicePanel.Children.Add(loadStatus);
                var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(250)};
                timer.Tick+=(s,e)=>{
                    string message=catalog.GemmaStatus;
                    if(message!=loadStatus.Text)loadStatus.Text=message;
                };
                dialog.Loaded+=(s,e)=>timer.Start();
                dialog.Closed+=(s,e)=>timer.Stop();
            }
            var threshold=Label("");body.Children.Add(threshold);
            var slider=new Slider {Minimum=1,Maximum=99,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,0,0,6)};
            System.Windows.Automation.AutomationProperties.SetName(slider,"AI suggestion threshold in percent");
            var guidance=Label("");
            Action showThreshold=()=>threshold.Text="Suggestion threshold  "+((int)slider.Value).ToString(CultureInfo.InvariantCulture)+"%";
            Action showModel=()=>{
                bool gemma=model.SelectedIndex==1;
                devicePanel.Visibility=gemma ? Visibility.Visible : Visibility.Collapsed;
                slider.Value=gemma ? gemmaThreshold : marqoThreshold;showThreshold();
                updateCriteriaGuidance();
            };
            updateCriteriaGuidance=()=>{
                bool gemma=model.SelectedIndex==1;
                guidance.Text=gemma && criteria!="" ? "The suggestion threshold applies to the five fixed category scores. Run Gemma again after changing the criteria." : gemma ? "The 55% default includes the ordinary swimwear tier. Raise it to 80% to focus on nudity, sexualized lingerie, and uncertain cases. Gemma is slower and uses more memory." : "The 7% default favors catching more images. Raise it to narrow the review queue. Changing the threshold reuses saved Marqo scores.";
            };
            slider.ValueChanged+=(s,e)=>showThreshold();model.SelectionChanged+=(s,e)=>showModel();showModel();
            body.Children.Add(slider);body.Children.Add(guidance);
            var actions=Bar();body.Children.Add(actions);
            Action<bool> save=run=>Guard(()=>{
                int percent=(int)slider.Value;
                bool gemma=model.SelectedIndex==1;
                bool cpuOnly=device.SelectedIndex==1;
                bool criteriaChanged=gemma && criteria!=savedCriteria;
                Queue(engine.RequestDraftThreshold(percent,gemma,cpuOnly,criteriaChanged ? criteria : null),result=>{
                    if(!result.Success){Message("Could not save AI settings. "+result.Error);return;}
                    if(gemma){gemmaThreshold=percent;gemmaCpuOnly=cpuOnly;if(criteriaChanged)gemmaCriteria=criteria;}else marqoThreshold=percent;
                    useGemma=gemma;
                    if(BucketKeys[bucket]==DraftReview.Bucket)NewPage(1);else InvalidateView();
                    Message(result.Value);
                    if(run && Ready)Guard(()=>StartDraftReview(gemma,cpuOnly));
                });
                dialog.Close();
            });
            var start=ActionButton("Save and run",()=>save(true));
            start.IsEnabled=task==null && !engine.Status.RunningScan && !engine.DraftReviewActive;
            start.ToolTip="Scores new or changed previews with the selected local model.";
            actions.Children.Add(start);actions.Children.Add(ActionButton("Save settings",()=>save(false)));
            var cancel=ActionButton("Cancel",dialog.Close);cancel.IsCancel=true;actions.Children.Add(cancel);
            dialog.ShowDialog();
        }

        void StartDraftReview(bool gemma,bool cpuOnly){
            draftTask=engine.RequestDraftReview(gemma,cpuOnly);Message((gemma ? "Gemma" : "Marqo")+" draft check queued…");
            Queue(draftTask,result=>{
                draftTask=null;
                Message(result.Success ? result.Value : result.Error);
            });
        }

        internal void ShowSettingsCogMenu(Button target) {
            var menu=CreateThemedMenu();
            var openCatalog=new MenuItem {Header="📂 Open or create catalog…",IsEnabled=Button("CatalogButton").IsEnabled};
            openCatalog.Click+=(s,e)=>Guard(ChooseDatabase);
            menu.Items.Add(openCatalog);
            menu.Items.Add(new Separator());

            var scanSub=new MenuItem {Header="⚙ Scan settings ("+Mode()+" · "+Number("WorkerBox")+" workers)"};
            string[] modes={"Quick","Deep","Custom"};
            for(int i=0;i<modes.Length;i++){
                int idx=i;
                bool active=Combo("ModeBox").SelectedIndex==idx;
                var mItem=new MenuItem {Header="Scan depth: "+modes[i]+(active ? "  (Active)" : "")};
                mItem.Click+=(s,e)=>Guard(()=>{Combo("ModeBox").SelectedIndex=idx;});
                scanSub.Items.Add(mItem);
            }
            scanSub.Items.Add(new Separator());
            Menu(scanSub,"🗂 Choose file types & families…",ChooseFormats);
            Menu(scanSub,"⚡ Scan options (AppData, ZIPs, Renamed files, Full SHA-256)…",ChooseOptions);
            var workersSub=new MenuItem {Header="👷 Worker threads ("+Number("WorkerBox")+")"};
            for(int w=1;w<=8;w++){
                int count=w;
                bool active=Number("WorkerBox")==count;
                var wItem=new MenuItem {Header=count+" worker"+(count==1 ? "" : "s")+(active ? "  (Active)" : "")};
                wItem.Click+=(s,e)=>Guard(()=>{Combo("WorkerBox").SelectedItem=count;});
                workersSub.Items.Add(wItem);
            }
            scanSub.Items.Add(workersSub);
            var ageSub=new MenuItem {Header="📅 Skip files older than ("+(Box("AgeBox").Text.Trim()=="0" ? "All ages" : Box("AgeBox").Text.Trim()+" yrs")+")"};
            foreach(int yrs in new[]{0,1,2,3,5,10}){
                int y=yrs;
                bool active=Box("AgeBox").Text.Trim()==y.ToString(CultureInfo.InvariantCulture);
                string label=(y==0 ? "0 years (Include all files)" : y+" year"+(y==1 ? "" : "s"))+(active ? "  (Active)" : "");
                var aItem=new MenuItem {Header=label};
                aItem.Click+=(s,e)=>Guard(()=>{Box("AgeBox").Text=y.ToString(CultureInfo.InvariantCulture);});
                ageSub.Items.Add(aItem);
            }
            ageSub.Items.Add(new Separator());
            Menu(ageSub,"Custom age limit…",PromptAgeFilter);
            scanSub.Items.Add(ageSub);
            menu.Items.Add(scanSub);

            var previewTasks=new MenuItem {Header="🖼 Preview tasks…",IsEnabled=Ready};
            previewTasks.Click+=(s,e)=>Guard(PreviewTasks);
            menu.Items.Add(previewTasks);
            var draftCheck=new MenuItem {Header="AI draft check…",IsEnabled=Button("AiDraftButton").IsEnabled};
            draftCheck.Click+=(s,e)=>Guard(ShowDraftReview);menu.Items.Add(draftCheck);

            var exportReport=new MenuItem {Header="📊 Export review report…",IsEnabled=Ready};
            exportReport.Click+=(s,e)=>Guard(Report);
            menu.Items.Add(exportReport);

            var maintSub=new MenuItem {Header="🛠 Care & maintenance",IsEnabled=Ready};
            PopulateMaintenanceItems(maintSub);
            menu.Items.Add(maintSub);

            menu.Items.Add(new Separator());
            var appearSub=new MenuItem {Header="🎨 Appearance / color modes",IsEnabled=!SystemParameters.HighContrast};
            string currentTheme=CurrentThemeId();
            foreach(var theme in Themes){
                var def=theme;
                var tItem=new MenuItem {Header=def.Label+(def.Id==currentTheme ? "  (Active)" : "")};
                tItem.Click+=(s,e)=>Guard(()=>SetTheme(def.Id));
                appearSub.Items.Add(tItem);
            }
            menu.Items.Add(appearSub);

            menu.Items.Add(new Separator());
            var logItem=new MenuItem {Header="📋 Activity log…",IsEnabled=Ready};
            logItem.Click+=(s,e)=>Guard(()=>Queue(engine.RequestLog(),r=>{if(r.Success)ShowText("Log",r.Value);else Message(r.Error);}));
            menu.Items.Add(logItem);

            Menu(menu,"💡 Guide & shortcuts",()=>Button("HelpButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
            Menu(menu,"♥ About Media Review",About);
            menu.PlacementTarget=target;menu.IsOpen=true;
        }
        string PromptSource(out bool acceptCandidates) {
            var body=new StackPanel {Margin=new Thickness(16)};var dialog=Dialog("Reconnect all sources",body,650,345);
            body.Children.Add(Label("Enter the actual media root. You can use a UNC share, mapped drive, or the local folder on the file server."));
            var path=Editor(Box("SourceBox").Text,32767);body.Children.Add(path);
            // Off by default: a sampled-hash match must never be bound to a saved review without someone choosing that.
            var candidates=new CheckBox {Content="Also accept quick candidate matches (not full-file proof)",Margin=new Thickness(4,10,4,0),
                ToolTip="A quick candidate is a larger file matched on sampled parts only. Leave this off to bind full matches only. A candidate that is already bound for the same unchanged file at this location stays bound; any other candidate stays unverified until you rescan the new location or run Reconnect all again with this option."};
            body.Children.Add(candidates);
            var actions=Bar();body.Children.Add(actions);
            actions.Children.Add(ActionButton("Browse…",()=>{string selected=ChooseFolder("Choose the replacement root",path.Text);if(selected!=null)path.Text=selected;}));
            var ok=ActionButton("Reconnect",()=>{if(!String.IsNullOrWhiteSpace(path.Text))dialog.DialogResult=true;});ok.IsDefault=true;actions.Children.Add(ok);
            actions.Children.Add(ActionButton("Cancel",dialog.Close));
            bool accepted=dialog.ShowDialog()==true;
            acceptCandidates=accepted && candidates.IsChecked==true;
            return accepted ? path.Text.Trim() : null;
        }
        void RetentionLimits(OriginalStorageLimits limits) {
            var body=new StackPanel {Margin=new Thickness(16)};var dialog=Dialog("Retention limits",body,525,355);
            body.Children.Add(Label("Use 0 for no fixed cap. Windows free space is checked for every capture."));
            body.Children.Add(Label("Database cap in GB"));var database=Editor((limits.DatabaseBytes/1000000000m).ToString(CultureInfo.CurrentCulture),24);body.Children.Add(database);
            body.Children.Add(Label("Individual original cap in GB"));var original=Editor((limits.FileBytes/1000000000m).ToString(CultureInfo.CurrentCulture),24);body.Children.Add(original);
            var feedback=Label("");body.Children.Add(feedback);var actions=Bar();body.Children.Add(actions);
            actions.Children.Add(ActionButton("Save",()=>{
                double db,file;if(!Double.TryParse(database.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out db) || !Double.TryParse(original.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out file)){feedback.Text="Enter a valid number for each limit.";return;}
                try{Work(engine.RequestOriginalLimits(db,file),"Save retention limits");dialog.Close();}catch(Exception error){feedback.Text=error.Message;}
            }));
            actions.Children.Add(ActionButton("Cancel",dialog.Close));dialog.ShowDialog();
        }

        void ChangePassword() {
            if(!engine.VaultExists)throw new InvalidOperationException("Create an originals vault first.");
            byte[] current=Password("Current originals password","Enter the current originals password.",false);
            if(current==null)return;
            byte[] replacement=null;bool submitted=false;
            try {
                replacement=Password("New originals password","Choose a new password. Earlier copies keep the password they were saved with.",true);
                if(replacement==null)return;
                if(protectedWindow!=null)protectedWindow.Close();
                Work(engine.RequestVaultPasswordChange(current,replacement,Id()),"Change originals password");submitted=true;
            }finally{if(!submitted){Array.Clear(current,0,current.Length);if(replacement!=null)Array.Clear(replacement,0,replacement.Length);}}
        }
        void ChangeDefaultSource(string path) {
            if(savedPreferences==null || savedPreferences.ReadOnly)throw new InvalidOperationException("Load writable preferences before changing the default source.");
            string previous=options.DefaultSourceRoot;
            options.DefaultSourceRoot=String.IsNullOrEmpty(path) ? "" : Paths.Source(path);
            try {
                string request=SavePreferences(engine);string signature=options.Signature;
                if(request==null)return;
                Queue(request,r=>{if(r.Success){preferencesSignature=signature;Message("Default source saved for this database.");}else{options.DefaultSourceRoot=previous;Message(r.Error);}});
                Message("Saving default source…");
            }catch{options.DefaultSourceRoot=previous;throw;}
        }
        void Menu(ItemsControl menu,string label,Action action){var item=new MenuItem {Header=label};item.Click+=(s,e)=>Guard(action);menu.Items.Add(item);}
        void ExportPackage(){
            var chosen=Selected();if(chosen.Length==0 || chosen.Any(c=>c.Record.Status!="EVIDENCE"))throw new InvalidOperationException("Select Evidence cards to package.");
            string path=SaveFile("Evidence package (*.mcrevidence)|*.mcrevidence","Evidence.mcrevidence");
            if(path!=null)Work(engine.RequestEvidencePackageExport(chosen.Select(c=>c.Record.GroupId).ToArray(),path,Id()),"Export Evidence package");
        }
    }
}
