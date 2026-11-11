using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaReviewer {
    internal sealed class WorkspacePreferences {
        public WorkspacePreferences(){}
        public bool Dark {get;set;}
        public string Theme {get;set;}
        public bool Thumbnails {get;set;}
        public bool SidebarCollapsed {get;set;}
        public bool PreviewCollapsed {get;set;}
        public double PreviewWidth {get;set;}
        public bool TopCollapsed {get;set;}
        public double TileSize {get;set;}
        public double[] Bounds {get;set;}

        static string FilePath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Media Compliance Reviewer","workspace.json");}}
        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer {MaxJsonLength=4096,RecursionLimit=8};}
        internal static WorkspacePreferences Load() {
            try {
                using(var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var input=new BinaryReader(stream)) {
                    byte[] bytes=input.ReadBytes(4097);
                    if(bytes.Length<=4096)return Serializer().Deserialize<WorkspacePreferences>(Encoding.UTF8.GetString(bytes))??new WorkspacePreferences();
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(SecurityException){}catch(ArgumentException){}catch(InvalidOperationException){}
            return new WorkspacePreferences();
        }

        internal void Save() {
            string path=FilePath,partial=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            bool owned=false;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var stream=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    owned=true;
                    using(var output=new StreamWriter(stream,new UTF8Encoding(false)))output.Write(Serializer().Serialize(this));
                }
                if(File.Exists(path))File.Replace(partial,path,null);
                else File.Move(partial,path);
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(SecurityException){}
            finally{if(owned)try{File.Delete(partial);}catch(IOException){}catch(UnauthorizedAccessException){}catch(SecurityException){}}
        }
    }

    sealed partial class ReviewerWindow {
        const double PreviewMinWidth=190,PreviewDefaultWidth=218,PreviewMaxWidth=720;
        enum ReviewView { List, Thumbnails, Quick }
        ReviewView display;
        WorkspacePreferences workspace;
        ReviewFullScreen quickFullScreen;
        bool persistWorkspace;
        int quickIndex;
        string columnSort;
        bool columnDescending;
        MediaRow quickSource;
        int quickDecodeSide;
        MediaRow quickVideoRow;
        VideoPlaybackControls quickPlayer;
        internal VideoPlaybackControls QuickPlayer {get{return quickPlayer;}}
        double quickZoom=1.0,quickPanX,quickPanY;
        int quickRotation;
        static string WindowsReviewerLabel(){
            string name=Environment.UserDomainName+"\\"+Environment.UserName;
            return name.Length<=128 ? name : Environment.UserName.Length<=128 ? Environment.UserName : "";
        }

        void SetUpWorkspace(bool persist) {
            persistWorkspace=persist;
            workspace=persist ? WorkspacePreferences.Load() : new WorkspacePreferences();
            ApplyTheme();
            var list=Get<ListView>("ReviewList");list.ItemsSource=cards;
            list.AddHandler(GridViewColumnHeader.ClickEvent,new RoutedEventHandler(SortColumn));
            list.MouseDoubleClick+=(s,e)=>{
                var element=e.OriginalSource as DependencyObject;
                var item=element==null ? null : ItemsControl.ContainerFromElement(list,element) as ListViewItem;
                var card=item==null ? null : item.Content as CardItem;
                if(Ready && card!=null){e.Handled=true;Guard(()=>Inspect(card.Record));}
            };
            var gallery=Get<ItemsControl>("CardList");
            gallery.PreviewMouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount!=2 || !Ready)return;
                var element=e.OriginalSource as DependencyObject;
                for(var control=element as FrameworkElement;control!=null && control!=gallery;control=VisualTreeHelper.GetParent(control) as FrameworkElement)
                    if(control is ButtonBase)return;
                var container=element==null ? null : ItemsControl.ContainerFromElement(gallery,element) as FrameworkElement;
                var card=container==null ? null : container.DataContext as CardItem;
                if(card!=null){e.Handled=true;Guard(()=>Inspect(card.Record));}
            };
            for(int i=0;i<BucketKeys.Length;i++){
                int index=i;Click("Queue"+i,()=>Combo("ViewBox").SelectedIndex=index);
            }
            Click("SettingsCogButton",()=>ShowSettingsCogMenu(Button("SettingsCogButton")));
            Click("ScanCogButton",()=>ShowSettingsCogMenu(Button("ScanCogButton")));
            Click("ThemeButton",()=>{
                if(!persistWorkspace){CycleTheme();return;}
                ShowAppearanceMenu(Button("ThemeButton"));
            });
            Click("ListModeButton",()=>SetView(ReviewView.List));
            Click("ThumbnailsModeButton",()=>SetView(ReviewView.Thumbnails));
            Click("QuickModeButton",()=>OpenQuick(FocusedCard()));
            Click("RefreshViewButton",InvalidateView);
            Click("QueueSidebarButton",()=>{workspace.SidebarCollapsed=!workspace.SidebarCollapsed;UpdateWorkspace();});
            Click("TopControlsButton",()=>{workspace.TopCollapsed=!workspace.TopCollapsed;UpdateWorkspace();});
            Click("PreviewPaneButton",()=>{workspace.PreviewCollapsed=!workspace.PreviewCollapsed;UpdateWorkspace();});
            Click("PaneDetailsButton",()=>{var card=FocusedCard();if(card!=null)Details(card.Record);});
            Click("PaneInspectButton",()=>{var card=FocusedCard();if(card!=null)Inspect(card.Record);});
            Click("PaneOpenButton",()=>{var card=FocusedCard();if(card!=null)Work(engine.RequestOpenOriginal(card.Record),"Open original");});
            Click("PaneCaseButton",()=>{var card=FocusedCard();if(card!=null)Work(engine.RequestCaseSheet(card.Record),"Case sheet",r=>ShowText("Non-compliant case sheet",r.Value));});
            Click("QuickPreviousButton",()=>{FocusQuick();RunQuickAction(Key.Left);});
            Click("QuickNextButton",()=>{FocusQuick();RunQuickAction(Key.Right);});
            Click("QuickNonCompliantButton",()=>{FocusQuick();RunQuickAction(Key.Down);});
            Click("QuickCompliantButton",()=>{FocusQuick();RunQuickAction(Key.Up);});
            Click("QuickUndoButton",()=>{FocusQuick();UndoLastReview();});
            quickFullScreen=new ReviewFullScreen(Window);
            Click("QuickFullScreenButton",ToggleQuickFullScreen);
            Click("QuickInspectButton",()=>{var card=QuickCard();if(card!=null)Inspect(card.Record);});
            Click("QuickZoomInButton",()=>{FocusQuick();AdjustQuickZoom(1.25);});
            Click("QuickZoomOutButton",()=>{FocusQuick();AdjustQuickZoom(0.8);});
            Click("QuickZoomResetButton",()=>{FocusQuick();ResetQuickTransform();});
            Click("QuickRotateLeftButton",()=>{FocusQuick();RotateQuick(-90);});
            Click("QuickRotateRightButton",()=>{FocusQuick();RotateQuick(90);});
            SetUpQuickGestures();
            Window.PreviewKeyDown+=ReviewKey;
            Get<Grid>("ReviewArea").SizeChanged+=(s,e)=>{
                if(e.WidthChanged && Get<GridSplitter>("PreviewDivider").Visibility==Visibility.Visible)UpdatePreviewWidthLimit();
            };
            Window.SourceInitialized+=(s,e)=>RestoreBounds();
            Get<Slider>("ThumbnailSize").Value=workspace.TileSize>=160 && workspace.TileSize<=320 ? workspace.TileSize : 220;
            SetView(workspace.Thumbnails ? ReviewView.Thumbnails : ReviewView.List);
        }

        internal sealed class ThemeDefinition {
            internal string Id,Label;
            internal bool IsDark;
            internal string[] Colors;
        }
        internal static readonly ThemeDefinition[] Themes=new[]{
            new ThemeDefinition {Id="warm-light",Label="☀ Warm Light (Cream & Terracotta)",IsDark=false,
                Colors=new[]{"#FAF5EE","#FFFDF9","#33261E","#7D6B5D","#F2EAE0","#E5D9C8","#2C2018","#FBF5EE","#4A3528","#D97742","#C86432","#FFFDF9","#E3F3E8","#1E6339","#FCE8E4","#9E2B25","#FEF3E7","#FDE68A","#78350F"}},
            new ThemeDefinition {Id="cozy-dark",Label="🌙 Cozy Dark (Warm Mocha)",IsDark=true,
                Colors=new[]{"#1B1613","#27201C","#F7EFE6","#B5A292","#201A16","#3E322B","#15110E","#F7EFE6","#382B23","#F4A261","#E07A5F","#FFFFFF","#1F3A2B","#BCE5CA","#482323","#FAD1D1","#362820","#6B4415","#FDE68A"}},
            new ThemeDefinition {Id="midnight-dark",Label="🌑 Midnight Dark (Slate & Sky)",IsDark=true,
                Colors=new[]{"#0F141C","#18202C","#ECF2F8","#94A3B8","#131A24","#2D3B4E","#0B0F17","#ECF2F8","#1E293B","#38BDF8","#0284C7","#FFFFFF","#133829","#86EFAC","#451A23","#FCA5A5","#1E2D40","#713F12","#FEF08A"}},
            new ThemeDefinition {Id="neon-green",Label="💻 Terminal Matrix (Dark + Neon Green)",IsDark=true,
                Colors=new[]{"#070B07","#0E150E","#39FF14","#22C55E","#0A100A","#1E3F20","#050805","#39FF14","#132A15","#00FF66","#15803D","#39FF14","#0F2E18","#4ADE80","#3B1219","#FF6B6B","#142E16","#3F320A","#FACC15"}},
            new ThemeDefinition {Id="synthwave-dark",Label="⚡ Synthwave Night (Dark + Neon Cyan)",IsDark=true,
                Colors=new[]{"#120D1D","#1C152E","#E0F2FE","#C084FC","#161024","#3B2854","#0D0916","#F0F9FF","#2E1F47","#06B6D4","#D946EF","#FFFFFF","#113634","#5EEAD4","#4C152E","#FDA4AF","#2A1B4E","#5C3409","#FDE047"}},
            new ThemeDefinition {Id="forest-sage",Label="🌲 Forest Sage (Soft Earthy Green)",IsDark=false,
                Colors=new[]{"#F1F5F0","#FAFCF9","#1C2D20","#5A7060","#E4ECE2","#CBD8C8","#18281D","#F2F7F2","#2A4231","#40916C","#2D6A4F","#FFFFFF","#DCFCE7","#166534","#FEE2E2","#991B1B","#E3F2E8","#FEF3C7","#92400E"}},
            new ThemeDefinition {Id="nordic-light",Label="❄ Nordic Daylight (Cool Frost)",IsDark=false,
                Colors=new[]{"#F3F6FA","#FFFFFF","#1E293B","#64748B","#E8EEF5","#D4DEE9","#1E293B","#F8FAFC","#334155","#0284C7","#0369A1","#FFFFFF","#E0F2FE","#0369A1","#FFE4E6","#BE123C","#E6F2FF","#FEF3C7","#92400E"}}
        };

        string CurrentThemeId() {
            if(!String.IsNullOrEmpty(workspace.Theme) && Themes.Any(t=>t.Id==workspace.Theme))return workspace.Theme;
            return workspace.Dark ? "cozy-dark" : "warm-light";
        }
        internal void SetTheme(string id) {
            var def=Themes.FirstOrDefault(t=>t.Id==id)??Themes[0];
            workspace.Theme=def.Id;
            workspace.Dark=def.IsDark;
            ApplyTheme();
            if(persistWorkspace)workspace.Save();
        }
        internal void CycleTheme() {
            string current=CurrentThemeId();
            int index=Array.FindIndex(Themes,t=>t.Id==current);
            SetTheme(Themes[(index+1)%Themes.Length].Id);
        }

        void ApplyTheme() {
            string[] keys={"PageBrush","SurfaceBrush","TextBrush","MutedBrush","InsetBrush","BorderBrush","SidebarBrush","SidebarTextBrush","SidebarActiveBrush","FocusBrush","PrimaryBrush","PrimaryTextBrush","GoodBrush","GoodTextBrush","BadBrush","BadTextBrush","SelectedCardBrush","BadgeWarnBrush","BadgeWarnTextBrush"};
            var def=Themes.FirstOrDefault(t=>t.Id==CurrentThemeId())??Themes[0];
            workspace.Theme=def.Id;workspace.Dark=def.IsDark;
            Color[] contrast={SystemColors.WindowColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.HighlightColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.WindowTextColor,SystemColors.WindowColor,SystemColors.WindowColor,SystemColors.WindowTextColor};
            for(int i=0;i<keys.Length;i++){
                var brush=new SolidColorBrush(SystemParameters.HighContrast ? contrast[i] : (Color)ColorConverter.ConvertFromString(def.Colors[i]));
                brush.Freeze();Window.Resources[keys[i]]=brush;
            }
            var list=Get<ListView>("ReviewList");
            if(list!=null && !SystemParameters.HighContrast){
                list.Resources[SystemColors.HighlightBrushKey]=Window.Resources["PrimaryBrush"];
                list.Resources[SystemColors.HighlightTextBrushKey]=Window.Resources["PrimaryTextBrush"];
                list.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=Window.Resources["PrimaryBrush"];
                list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=Window.Resources["PrimaryTextBrush"];
            }
            Button("ThemeButton").Content="🎨 Appearance";
            Button("ThemeButton").ToolTip="Appearance · "+def.Label;
            Button("ThemeButton").IsEnabled=!SystemParameters.HighContrast;
        }

        void SetView(ReviewView mode) {
            if(display==ReviewView.Quick && mode!=display){quickFullScreen.Set(false);CancelQuickGesture();ResetQuickPreview();}
            display=mode;
            if(mode!=ReviewView.Quick)workspace.Thumbnails=mode==ReviewView.Thumbnails;
            UpdateControls();
        }
        void OpenQuick(CardItem card){
            if(!Ready || card==null)return;
            int index=cards.IndexOf(card);if(index<0)return;
            CancelQuickGesture();ResetQuickPreview();quickIndex=index;SetView(ReviewView.Quick);FocusQuick();
        }
        void FocusQuick(){if(display==ReviewView.Quick)Get<FrameworkElement>("QuickImageHost").Focus();}

        int QuickPosition {get{return Math.Min(Math.Max(0,quickIndex),cards.Count-1);}}
        CardItem QuickCard(){return QuickPosition<0 ? null : cards[QuickPosition];}
        CardItem FocusedCard(){return display==ReviewView.Quick ? QuickCard() : cards.FirstOrDefault(c=>c.IsSelected)??cards.FirstOrDefault();}
        void ResetQuickPreview(){
            if(quickSource!=null && inspector==null)loader.CancelInspect(inspection.Advance());
            quickSource=null;quickDecodeSide=0;Get<Image>("QuickDetailImage").Source=null;
            ResetQuickTransform();
            ClearQuickVideo();
        }
        void ResetQuickTransform(){
            quickZoom=1.0;quickPanX=quickPanY=0;quickRotation=0;
            ApplyQuickTransform();
        }
        void AdjustQuickZoom(double factor){
            if(display!=ReviewView.Quick || QuickCard()==null)return;
            quickZoom=Math.Max(1.0,Math.Min(6.0,quickZoom*factor));
            if(quickZoom<=1.02){quickZoom=1.0;quickPanX=quickPanY=0;}
            ApplyQuickTransform();
        }
        // Advancing the view generation drops in-flight thumbnail results, so every card must be allowed to
        // request its image again or it stays on "Loading preview" until the next page load.
        void InvalidateView(){view.Advance();refresh=true;foreach(var card in cards)card.ImageQueued=false;}
        void RotateQuick(int deltaDegrees){
            if(display!=ReviewView.Quick || QuickCard()==null)return;
            quickRotation=((quickRotation+deltaDegrees)%360+360)%360;
            ApplyQuickTransform();
        }
        void ApplyQuickTransform(){
            var surface=Get<FrameworkElement>("QuickZoomSurface");
            if(surface==null)return;
            surface.LayoutTransform=quickRotation==0 ? Transform.Identity : new RotateTransform(quickRotation);
            if(quickZoom<=1.01){
                surface.RenderTransform=Transform.Identity;
            }else{
                var group=new TransformGroup();
                group.Children.Add(new ScaleTransform(quickZoom,quickZoom));
                group.Children.Add(new TranslateTransform(quickPanX,quickPanY));
                surface.RenderTransform=group;
            }
            var resetBtn=Button("QuickZoomResetButton");
            if(resetBtn!=null)resetBtn.Content=quickZoom<=1.01 && quickRotation==0 ? "Fit" : Math.Round(quickZoom*100)+"%"+(quickRotation==0 ? "" : " · "+quickRotation+"°");
        }

        void ToggleQuickFullScreen(){
            if(!quickFullScreen.Active && (display!=ReviewView.Quick || QuickCard()==null))return;
            quickFullScreen.Set(!quickFullScreen.Active);
            UpdateWorkspace();FocusQuick();
        }

        void ClearQuickVideo() {
            if(quickPlayer==null)return;
            quickPlayer.Dispose();
            quickPlayer=null;quickVideoRow=null;
            var host=Get<ContentControl>("QuickVideoHost");
            host.Content=null;host.Visibility=Visibility.Collapsed;
        }

        static bool CanPlayQuickVideo(MediaRow row) {
            return row!=null && Formats.IsPlayable(row.Extension) && row.Status!="EVIDENCE" && !row.SourceIsHistorical &&
                !String.IsNullOrEmpty(row.RootPath) && !String.IsNullOrEmpty(row.RelativePath) && !Archives.IsPath(row.RelativePath);
        }

        static bool SamePreviewSource(MediaRow first,MediaRow second) {
            return first!=null && first.GroupId==second.GroupId && first.ContentId==second.ContentId &&
                first.RootId==second.RootId && first.RootPath==second.RootPath && first.RelativePath==second.RelativePath &&
                first.FileIdentity==second.FileIdentity && first.Size==second.Size && first.ModifiedTicks==second.ModifiedTicks;
        }

        void UpdateQuickVideo(CardItem card) {
            var row=card==null ? null : card.Record;
            if(!Ready || display!=ReviewView.Quick || inspector!=null || !CanPlayQuickVideo(row)) {
                ClearQuickVideo();
                return;
            }
            if(SamePreviewSource(quickVideoRow,row))return;
            ResetQuickPreview();
            quickPlayer=CreateVideoPlayer(Get<Grid>("QuickVideoSurface"),row);
            quickPlayer.AttachPoster(Get<Image>("QuickBaseImage"));
            quickPlayer.ToggleFullScreen=ToggleQuickFullScreen;
            quickPlayer.AttachHoverHost(Get<Grid>("QuickStage"),Get<Grid>("QuickImageHost"));
            quickVideoRow=row;
            var host=Get<ContentControl>("QuickVideoHost");
            host.Content=quickPlayer;host.Visibility=Visibility.Visible;
            // The video itself is opened only when the reviewer presses Play. Opening it starts the Windows media
            // pipeline inside this process on a file from the scanned drive; the saved poster covers the wait.
        }
        void LoadQuickPreview() {
            if(display!=ReviewView.Quick || inspector!=null)return;
            var card=QuickCard();var row=card==null ? null : card.Record;
            if(row==null || row.Status=="EVIDENCE" || row.SourceIsHistorical || String.IsNullOrEmpty(row.RootPath) || String.IsNullOrEmpty(row.RelativePath)){ResetQuickPreview();return;}
            if(CanPlayQuickVideo(row))return;
            var host=Get<FrameworkElement>("QuickImageHost");
            int desired=Math.Min(InspectionPlan.MaxSide,Math.Max(256,(int)Math.Ceiling(Math.Max(host.ActualWidth,host.ActualHeight))));
            bool same=SamePreviewSource(quickSource,row);
            if(same && desired<=quickDecodeSide+32)return;
            if(!same)ResetQuickPreview();
            quickSource=row;quickDecodeSide=desired;
            loader.QueueInspect(row,inspection.Advance(),desired);
        }
        void ApplyQuickPreview(MediaLoadResult result) {
            var current=QuickCard();
            if(display!=ReviewView.Quick || current==null || result.GroupId!=current.Record.GroupId || result.ContentId!=current.Record.ContentId)return;
            if(result.Image!=null)Get<Image>("QuickDetailImage").Source=result.Image;
            else if(!String.IsNullOrEmpty(result.Error))Message(result.Error);
        }
        bool CanMoveQuick(int direction) {
            return Ready && cards.Count!=0 && (direction<0 ? QuickPosition>0 || page>1 : QuickPosition+1<cards.Count || page<pageCount);
        }
        void MoveQuick(int direction){
            if(!CanMoveQuick(direction))return;
            CancelQuickGesture();
            int next=QuickPosition+direction;
            if(next>=0 && next<cards.Count){ResetQuickPreview();quickIndex=next;UpdateControls();return;}
            int target=page+direction;
            if(target<1 || target>pageCount)return;
            NewPage(target);quickIndex=direction<0 ? Number("PageSizeBox")-1 : 0;
        }

        void ReviewWorkspaceItems(string target) {
            long generation=view.Current;
            if(display!=ReviewView.Quick){
                var chosen=Selected();
                if(chosen.Length==0)return;
                // After the decision is saved, move the highlight on so the next one needs no extra click: to the item
                // that slides into the reviewed item's place, or to the following item when the reviewed one stays listed.
                int first=chosen.Min(c=>cards.IndexOf(c)),last=chosen.Max(c=>cards.IndexOf(c));
                bool stays=target==BucketKeys[bucket];
                Review(target,chosen,r=>{
                    if(!r.Success || !view.Accept(generation) || display==ReviewView.Quick || cards.Any(c=>c.IsSelected))return;
                    if(!stays)SelectCardAt(first);else if(last+1<cards.Count)SelectCardAt(last+1);
                });
                return;
            }
            var card=QuickCard();if(card==null || !card.CanSelect)return;
            Review(target,new[]{card},r=>{
                if(!r.Success || !view.Accept(generation) || display!=ReviewView.Quick)return;
                if(target==BucketKeys[bucket] && ReferenceEquals(QuickCard(),card))MoveQuick(1);
                UpdateControls();
            },stillIntended:()=>display==ReviewView.Quick && view.Accept(generation) && ReferenceEquals(QuickCard(),card));
        }

        void ReviewKey(object sender,KeyEventArgs e) {
            if(e.Handled)return;
            if(quickFullScreen.Active && e.Key==Key.Escape){
                e.Handled=true;if(!e.IsRepeat)Guard(ToggleQuickFullScreen);return;
            }
            if(!Ready)return;
            for(var node=Keyboard.FocusedElement as DependencyObject;node!=null;node=node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if(node is TextBoxBase || node is PasswordBox || node is ComboBox)return;
            if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z && !e.IsRepeat){
                e.Handled=true;Guard(UndoLastReview);return;
            }
            if(Keyboard.Modifiers!=ModifierKeys.None)return;
            if(display==ReviewView.Quick && (e.Key==Key.F || e.Key==Key.F11)){
                e.Handled=true;if(!e.IsRepeat)Guard(ToggleQuickFullScreen);return;
            }
            if(display==ReviewView.Quick && e.Key==Key.Space && quickPlayer!=null && Get<Grid>("QuickImageHost").IsKeyboardFocusWithin){
                e.Handled=true;if(!e.IsRepeat)quickPlayer.TogglePlay();return;
            }
            Key direction=e.Key;
            if((direction==Key.Left || direction==Key.Right || direction==Key.Up || direction==Key.Down) &&
                display==ReviewView.Quick && Get<FrameworkElement>("QuickImageHost").IsKeyboardFocusWithin){
                e.Handled=true;if(!e.IsRepeat)Guard(()=>RunQuickAction(direction));return;
            }
            if(display==ReviewView.Quick && e.Key==Key.R && !e.IsRepeat){
                e.Handled=true;var quickFocus=FocusedCard();if(quickFocus!=null && !Formats.IsPlayable(quickFocus.Record.Extension))Guard(()=>RotateQuick(90));return;
            }
            if(e.IsRepeat || (e.Key!=Key.C && e.Key!=Key.N))return;
            if(display!=ReviewView.Quick && Selected().Length==0)return;
            e.Handled=true;Guard(()=>ReviewWorkspaceItems(e.Key==Key.C ? "COMPLIANT" : "NON_COMPLIANT"));
        }

        static readonly Dictionary<string,string> ColumnTitles=new Dictionary<string,string>{
            {"name","Name"},{"folder","Folder"},{"type","Type"},{"size","Size"},{"modified","Modified"},{"reviewer","Reviewed by"}
        };
        void UpdateColumnHeaders(){
            var grid=Get<ListView>("ReviewList").View as GridView;
            if(grid==null)return;
            foreach(var col in grid.Columns){
                var header=col.Header as GridViewColumnHeader;
                var tag=header==null ? null : header.Tag as string;
                string baseTitle;
                if(tag!=null && ColumnTitles.TryGetValue(tag,out baseTitle))
                    header.Content=columnSort==tag ? baseTitle+(columnDescending ? " ▼" : " ▲") : baseTitle;
            }
        }

        void SortColumn(object sender,RoutedEventArgs args){
            var header=args.OriginalSource as GridViewColumnHeader;
            var sort=header==null ? null : header.Tag as string;
            if(!Ready || sort==null)return;
            columnDescending=columnSort==sort ? !columnDescending : sort=="size" || sort=="modified";
            columnSort=sort;UpdateColumnHeaders();NewPage(1);
        }

        void UpdateWorkspace() {
            if(workspace==null)return;
            bool full=quickFullScreen!=null && quickFullScreen.Active;
            var chrome=full ? Visibility.Collapsed : Visibility.Visible;
            foreach(string name in new[]{"SidebarEdge","TopHeaderBar","TopControlsEdge","PaginationBar","StatusPanel"})
                Get<FrameworkElement>(name).Visibility=chrome;
            bool showSidebar=!workspace.SidebarCollapsed && !full;
            Get<FrameworkElement>("QueueSidebar").Visibility=showSidebar ? Visibility.Visible : Visibility.Collapsed;
            Get<FrameworkElement>("CompactQueues").Visibility=showSidebar ? Visibility.Collapsed : Visibility.Visible;
            SetPaneTab("QueueSidebarButton",showSidebar ? "Left" : "Right",showSidebar ? "Collapse left sidebar" : "Expand left sidebar");
            Get<FrameworkElement>("TopControls").Visibility=workspace.TopCollapsed || full ? Visibility.Collapsed : Visibility.Visible;
            SetPaneTab("TopControlsButton",workspace.TopCollapsed ? "Down" : "Up",workspace.TopCollapsed ? "Expand top controls" : "Collapse top controls");
            for(int i=0;i<BucketKeys.Length;i++){
                var queue=Button("Queue"+i);queue.Tag=bucket==i ? "Active" : null;queue.IsEnabled=Ready;
            }
            Get<ListView>("ReviewList").Visibility=display==ReviewView.List ? Visibility.Visible : Visibility.Collapsed;
            Get<ScrollViewer>("GalleryScroll").Visibility=display==ReviewView.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            Get<Slider>("ThumbnailSize").Visibility=display==ReviewView.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            var focus=FocusedCard();var preview=Get<FrameworkElement>("PreviewPane");preview.DataContext=focus;
            bool showPreviewTab=display!=ReviewView.Quick;
            ShowPreviewPane(!workspace.PreviewCollapsed && showPreviewTab);
            Button("PreviewPaneButton").Visibility=showPreviewTab ? Visibility.Visible : Visibility.Collapsed;
            Get<FrameworkElement>("PreviewEdge").Visibility=showPreviewTab ? Visibility.Visible : Visibility.Collapsed;
            SetPaneTab("PreviewPaneButton",workspace.PreviewCollapsed ? "Left" : "Right",workspace.PreviewCollapsed ? "Expand right preview" : "Collapse right preview");
            Button("PaneDetailsButton").IsEnabled=Button("PaneInspectButton").IsEnabled=Ready && focus!=null;
            bool video=focus!=null && Formats.IsPlayable(focus.Record.Extension);
            bool animation=focus!=null && focus.Record.Extension=="gif";
            Button("PaneInspectButton").Content=Button("QuickInspectButton").Content=animation ? "Inspect / animation" : video ? "🎬 Inspect / video" : "🔍 Inspect";
            Button("PaneInspectButton").ToolTip=Button("QuickInspectButton").ToolTip=video ? "Open video inspection with playback, seek and volume controls" : "Open full image inspection";
            Button("PaneOpenButton").IsEnabled=Ready && focus!=null && focus.Record.Status!="EVIDENCE" && focus.Record.LocationCount>0;
            Button("PaneCaseButton").Visibility=focus!=null && focus.Record.Status=="NON_COMPLIANT" ? Visibility.Visible : Visibility.Collapsed;
            Button("PaneCaseButton").IsEnabled=Ready;
            var quick=Get<FrameworkElement>("QuickPane");quick.DataContext=display==ReviewView.Quick ? focus : null;
            quick.Visibility=display==ReviewView.Quick ? Visibility.Visible : Visibility.Collapsed;
            if(display==ReviewView.Quick){
                Text("QuickPositionBadge",cards.Count==0 ? "No items" : String.Format("Item {0} of {1} on page {2}/{3} · {4}",QuickPosition+1,cards.Count,page,pageCount,BucketNames[bucket]));
            }
            UpdateQuickVideo(focus);
            if(quickPlayer!=null)quickPlayer.SetFullScreenState(full);
            bool hasQuickCard=Ready && focus!=null;
            Button("QuickFullScreenButton").Content=full ? "Exit full screen" : "Full screen";
            Button("QuickFullScreenButton").IsEnabled=full || hasQuickCard;
            Button("QuickNonCompliantButton").IsEnabled=Button("QuickCompliantButton").IsEnabled=hasQuickCard && focus.CanSelect;
            Button("QuickInspectButton").IsEnabled=hasQuickCard;
            Button("QuickZoomInButton").IsEnabled=Button("QuickZoomOutButton").IsEnabled=Button("QuickZoomResetButton").IsEnabled=Button("QuickRotateLeftButton").IsEnabled=Button("QuickRotateRightButton").IsEnabled=hasQuickCard && !video;
            Button("QuickPreviousButton").IsEnabled=CanMoveQuick(-1);
            Button("QuickNextButton").IsEnabled=CanMoveQuick(1);
            Button("ListModeButton").IsEnabled=display!=ReviewView.List;
            Button("ThumbnailsModeButton").IsEnabled=display!=ReviewView.Thumbnails;
            Button("QuickModeButton").IsEnabled=Ready && focus!=null && display!=ReviewView.Quick;
            Button("RefreshViewButton").IsEnabled=Ready;
        }

        void SetPaneTab(string name,string direction,string action) {
            var tab=Button(name);
            tab.Content=direction;
            tab.ToolTip=action;
            AutomationProperties.SetName(tab,action);
        }

        void ShowPreviewPane(bool show) {
            var pane=Get<FrameworkElement>("PreviewPane");
            Visibility visibility=show ? Visibility.Visible : Visibility.Collapsed;
            if(pane.Visibility==visibility)return;
            var divider=Get<GridSplitter>("PreviewDivider");
            var column=Get<ColumnDefinition>("PreviewColumn");
            if(show){
                double width=workspace.PreviewWidth;
                if(!(width>=PreviewMinWidth && width<=PreviewMaxWidth))width=PreviewDefaultWidth;
                UpdatePreviewWidthLimit();
                column.MinWidth=PreviewMinWidth;
                column.Width=new GridLength(width);
            }else{
                if(divider.IsDragging)divider.CancelDrag();
                workspace.PreviewWidth=column.Width.Value;
                column.MinWidth=0;column.MaxWidth=0;column.Width=new GridLength(0);
            }
            pane.Visibility=divider.Visibility=visibility;
        }

        void UpdatePreviewWidthLimit() {
            double width=Get<Grid>("ReviewArea").ActualWidth-Button("PreviewPaneButton").Width-330;
            Get<ColumnDefinition>("PreviewColumn").MaxWidth=Math.Max(PreviewMinWidth,Math.Min(PreviewMaxWidth,width));
        }

        void RestoreBounds() {
            if(workspace.Bounds==null || workspace.Bounds.Length!=4 || workspace.Bounds.Any(n=>Double.IsInfinity(n) || Double.IsNaN(n)))return;
            Rect screen=SystemParameters.WorkArea;
            var b=workspace.Bounds;
            Window.Width=Math.Max(Window.MinWidth,Math.Min(b[2],screen.Width));
            Window.Height=Math.Max(Window.MinHeight,Math.Min(b[3],screen.Height));
            Window.Left=Math.Max(screen.Left,Math.Min(b[0],screen.Right-Window.Width));
            Window.Top=Math.Max(screen.Top,Math.Min(b[1],screen.Bottom-Window.Height));
        }

        void SaveWorkspace() {
            if(!persistWorkspace)return;
            quickFullScreen.Set(false);
            Rect bounds=Window.RestoreBounds;
            if(!bounds.IsEmpty)workspace.Bounds=new[]{bounds.Left,bounds.Top,bounds.Width,bounds.Height};
            workspace.TileSize=Get<Slider>("ThumbnailSize").Value;
            if(Get<GridSplitter>("PreviewDivider").Visibility==Visibility.Visible)workspace.PreviewWidth=Get<ColumnDefinition>("PreviewColumn").Width.Value;
            workspace.Save();
        }
    }
}
