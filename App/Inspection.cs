using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaReviewer {
    sealed partial class ReviewerWindow {
        readonly Dictionary<Window,long> ordinaryDetails=new Dictionary<Window,long>();

        void Details(MediaRow row) {
            if(row.Status=="EVIDENCE"){Original(row);return;}
            var panel=new DockPanel {Margin=new Thickness(16)};var window=Dialog("Item details and notes",panel,825,660);
            var top=new StackPanel();Dock(panel,top,System.Windows.Controls.Dock.Top);
            var summary=Label("Loading details…");top.Children.Add(summary);
            var reviewer=Editor("",128);var notes=Editor("");notes.AcceptsReturn=true;notes.TextWrapping=TextWrapping.Wrap;
            notes.Height=112;notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            top.Children.Add(Label("Reviewer"));top.Children.Add(reviewer);top.Children.Add(Label("Notes"));top.Children.Add(notes);
            var tagBar=new WrapPanel {Margin=new Thickness(0,4,0,4)};
            foreach(string preset in new[]{"Personal media","Unauthorized file","Renamed extension","Game / archive asset","Needs follow-up"}){
                string p=preset;tagBar.Children.Add(ActionButton("+ "+p,()=>AppendNoteTag(notes,p)));
            }
            tagBar.Children.Add(ActionButton("Clear notes",()=>notes.Clear()));
            top.Children.Add(tagBar);
            var feedback=Label("");var actions=Bar();
            Dock(panel,actions,System.Windows.Controls.Dock.Bottom);Dock(panel,feedback,System.Windows.Controls.Dock.Bottom);
            var locationPanel=new DockPanel();
            var locationStatus=Label("Loading recorded locations…");Dock(locationPanel,locationStatus,System.Windows.Controls.Dock.Top);
            var locations=new ListView();
            var locationStyle=new Style(typeof(ListViewItem));
            locationStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("FullPath")));
            locations.ItemContainerStyle=locationStyle;
            locations.View=new GridView {Columns={
                new GridViewColumn {Header="Recorded relative path",Width=280,DisplayMemberBinding=new System.Windows.Data.Binding("Path")},
                new GridViewColumn {Header="Source root",Width=238,DisplayMemberBinding=new System.Windows.Data.Binding("RootPath")},
                new GridViewColumn {Header="Bytes",Width=74,DisplayMemberBinding=new System.Windows.Data.Binding("Size")},
                new GridViewColumn {Header="Card",Width=90,DisplayMemberBinding=new System.Windows.Data.Binding("CardLabel")},
                new GridViewColumn {Header="Historical",Width=76,DisplayMemberBinding=new System.Windows.Data.Binding("Historical")}}};
            locationPanel.Children.Add(locations);panel.Children.Add(locationPanel);
            bool live=true;
            var catalog=engine;
            InspectorData current=null;string cursor="",nextCursor="";Button save=null,next=null,previous=null;
            var history=new Stack<string>();var generation=new ViewGeneration();
            bool saving=false,detailsLoading=false,locationsLoading=false;
            long locationRevision=-1;
            Action updateEditors=()=>{
                bool editable=current!=null && !current.TextReadOnly && !saving && !detailsLoading;
                reviewer.IsReadOnly=notes.IsReadOnly=!editable;
                tagBar.IsEnabled=save.IsEnabled=editable;
            };
            Func<UiResult,bool> closeProtected=r=>{
                if(!r.ProtectedDetails)return false;
                window.Close();
                Message(r.Error);
                return true;
            };
            Action loadLocations=()=>{
                long requestedRevision=catalog.Status.Revision;
                string request=catalog.RequestLocations(row.GroupId,cursor);
                locationsLoading=true;locationRevision=requestedRevision;
                next.IsEnabled=previous.IsEnabled=false;long version=generation.Advance();
                Queue(request,r=>{
                    if(!live || !generation.Accept(version))return;
                    if(closeProtected(r))return;
                    locationsLoading=false;
                    previous.IsEnabled=history.Count>0;
                    if(!r.Success){locationStatus.Text=r.Error;return;}
                    locations.ItemsSource=r.Locations.Rows;nextCursor=r.Locations.NextAfter;next.IsEnabled=r.Locations.HasMore;
                    locationStatus.Text="Recorded locations · "+r.Locations.Rows.Count+" shown"+(r.Locations.HasMore ? " · more on the next page" : "");
                });
            };
            var refreshLocations=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(750)};
            refreshLocations.Tick+=(s,e)=>{
                if(!Ready || engine!=catalog || current==null || locationsLoading)return;
                var snapshot=catalog.Status;
                if(!snapshot.RunningScan && snapshot.Revision!=locationRevision)Guard(loadLocations);
            };
            window.Closed+=(s,e)=>{live=false;refreshLocations.Stop();ordinaryDetails.Remove(window);};
            Func<bool> dirty=()=>current!=null && (reviewer.Text!=current.Reviewer || notes.Text!=current.Notes);
            var detailsVersion=new ViewGeneration();
            Action<bool> load=keepDraft=>{
                if(saving || detailsLoading)return;
                string request=catalog.RequestInspector(row.GroupId,0);
                detailsLoading=true;
                updateEditors();
                long version=detailsVersion.Advance();
                Queue(request,r=>{
                    if(!live || !detailsVersion.Accept(version))return;
                    if(closeProtected(r))return;
                    detailsLoading=false;
                    if(!r.Success){feedback.Text=r.Error;updateEditors();return;}
                    bool preserveDraft=keepDraft && current!=null;
                    current=r.Inspector;
                    summary.Text="Group "+current.GroupId+" · Publication "+current.Sequence+" · "+current.Status+" · "+current.Size.ToString("N0")+" bytes\n"+
                        "Root: "+current.RootPath+"\n"+current.Algorithm+" v"+current.AlgorithmVersion+": "+current.Digest+"\nPreview: "+current.PreviewState;
                    if(!preserveDraft){reviewer.Text=current.Reviewer;notes.Text=current.Notes;}
                    else feedback.Text="Saved review reloaded. Your draft is still in the editor.";
                    updateEditors();cursor="";history.Clear();loadLocations();
                });
            };
            save=ActionButton("Save notes",()=>Guard(()=>{
                if(current==null || saving || detailsLoading || current.TextReadOnly)return;
                var request=new NotesRequest {GroupId=current.GroupId,ContentId=current.ContentId,ExpectedRevision=current.ReviewRevision,Reviewer=reviewer.Text,Notes=notes.Text,OperationId=Id()};
                Queue(catalog.RequestNotes(request),r=>{
                    if(!live || closeProtected(r))return;
                    saving=false;
                    updateEditors();
                    feedback.Text=r.Success ? "Notes saved." : r.Error;
                    if(r.Success)load(false);
                });
                saving=true;updateEditors();feedback.Text="Saving notes…";
            }));
            previous=ActionButton("Previous locations",()=>Guard(()=>{cursor=history.Pop();loadLocations();}));
            next=ActionButton("Next locations",()=>Guard(()=>{history.Push(cursor);cursor=nextCursor;loadLocations();}));
            previous.IsEnabled=next.IsEnabled=save.IsEnabled=false;
            actions.Children.Add(save);actions.Children.Add(ActionButton("Reload, keep draft",()=>Guard(()=>load(true))));
            actions.Children.Add(ActionButton("Discard draft",()=>Guard(()=>{if(!dirty() || MessageBox.Show(window,"Discard the unsaved edits?","Discard draft",MessageBoxButton.YesNo)==MessageBoxResult.Yes)load(false);}))); 
            actions.Children.Add(previous);actions.Children.Add(next);actions.Children.Add(ActionButton("Close",window.Close));
            var neighbors=new WrapPanel();top.Children.Insert(0,neighbors);
            foreach(int direction in new[]{-1,1}){
                int step=direction;
                neighbors.Children.Add(ActionButton(step<0 ? "Previous item" : "Next item",()=>Guard(()=>{
                    if(saving)return;
                    int at=cards.ToList().FindIndex(c=>c.Record.GroupId==row.GroupId),target=at+step;
                    if(at<0 || target<0 || target>=cards.Count)return;
                    if(dirty() && MessageBox.Show(window,"Leave this item and discard its unsaved edits?","Unsaved notes",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    MediaRow other=cards[target].Record;window.Close();Details(other);
                })));
            }
            updateEditors();ordinaryDetails.Add(window,row.GroupId);
            TrackDatabaseWindow(window);window.Show();Guard(()=>load(false));refreshLocations.Start();
        }

        ReviewFullScreen inspectFullScreen;
        Action clearInspector;

        void Inspect(MediaRow row) {
            ResetQuickPreview();
            if(row.Status=="EVIDENCE" || row.OriginalRetained && row.LocationCount==0){
                if(inspector!=null)inspector.Close();
                Original(row);return;
            }
            if(clearInspector!=null)clearInspector();
            var panel=new DockPanel {Margin=new Thickness(16)};
            bool opened=inspector!=null;
            var window=inspector;
            if(!opened){
                window=Dialog("Inspect · "+row.Name,panel,960,735);inspector=window;
                inspectFullScreen=new ReviewFullScreen(window);
                window.Closed+=(s,e)=>{
                    if(clearInspector!=null)clearInspector();
                    clearInspector=null;inspectFullScreen=null;inspector=null;
                };
            }else{
                window.Title="Inspect · "+row.Name;window.Content=panel;
            }
            var top=new StackPanel();Dock(panel,top,System.Windows.Controls.Dock.Top);
            var actions=new WrapPanel();top.Children.Add(actions);
            var reviewActions=new WrapPanel();top.Children.Add(reviewActions);
            var reviewer=Editor(row.Reviewer=="" ? Box("ReviewerBox").Text : row.Reviewer,128);reviewer.Width=135;
            var notes=Editor(row.Notes);notes.Width=220;
            reviewActions.Children.Add(Label("Reviewer"));reviewActions.Children.Add(reviewer);reviewActions.Children.Add(Label("Notes"));reviewActions.Children.Add(notes);
            var tagActions=new WrapPanel {Margin=new Thickness(0,2,0,2)};
            foreach(string preset in new[]{"Personal media","Unauthorized file","Renamed extension","Needs follow-up"}){
                string p=preset;tagActions.Children.Add(ActionButton("+ "+p,()=>AppendNoteTag(notes,p)));
            }
            top.Children.Add(tagActions);
            var feedback=Label("Loading source preview…");feedback.MinHeight=36;top.Children.Add(feedback);
            var image=new Image {Stretch=Stretch.Uniform};
            var preview=new ScrollViewer {Content=image,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
            preview.SetResourceReference(Control.BackgroundProperty,"InsetBrush");
            var footer=Bar();Dock(panel,footer,System.Windows.Controls.Dock.Bottom);footer.Children.Add(ActionButton("Close",window.Close));
            var mediaStage=new Grid {Background=Brushes.Transparent,ClipToBounds=true};
            var viewport=new Grid {Focusable=true,Background=Brushes.Transparent};
            viewport.Children.Add(preview);mediaStage.Children.Add(viewport);panel.Children.Add(mediaStage);
            System.Windows.Automation.AutomationProperties.SetName(viewport,"Media preview. Left and Right browse. Up marks Compliant. Down marks Non-compliant.");
            viewport.PreviewMouseLeftButtonDown+=(s,e)=>viewport.Focus();
            bool live=true;int side=0,sourceWidth=0,sourceHeight=0,rotation=0;
            string layout="Fit";double zoom=1;
            int position=positionOf(row);
            CardItem current=position>=0 ? cards[position] : new CardItem(row);
            Action<int> navigate=direction=>{
                if(!Ready || current.IsPending)return;
                int index=positionOf(row),next=index+direction;
                if(index>=0 && next>=0 && next<cards.Count)Inspect(cards[next].Record);
            };
            Action<string> classify=key=>{
                if(!Ready || current.IsPending)return;
                if(current.Record.ContentId!=row.ContentId || current.Record.ReviewRevision!=row.ReviewRevision){
                    feedback.Text="This item changed. Reopen it to review the latest version.";
                    return;
                }
                long generation=view.Current;
                int index=positionOf(row);
                CardItem next=(key=="COMPLIANT" || key=="NON_COMPLIANT") && index>=0 && index+1<cards.Count ? cards[index+1] : null;
                Review(key,new[]{current},r=>{
                    if(!live)return;
                    if(!r.Success){feedback.Text=r.Error;return;}
                    if(Ready && view.Accept(generation) && next!=null && cards.Contains(next))Inspect(next.Record);
                    else window.Close();
                },reviewer.Text,notes.Text,()=>live && Ready && view.Accept(generation));
            };
            for(int i=0;i<BucketKeys.Length;i++){
                string key=BucketKeys[i];
                if(key==DraftReview.Bucket)continue;
                reviewActions.Children.Add(ActionButton(BucketNames[i],()=>Guard(()=>classify(key))));
            }

            if(position>=0)image.Source=cards[position].Image;
            bool video=Formats.IsPlayable(row.Extension);
            if(video)feedback.Text=Formats.UsesPlaybackProxy(row.Extension) ?
                "Press Play to prepare an offline playback copy. Retry loads a fresh poster." :
                "Video inspection · preparing playback. Retry loads a fresh source poster.";
            Action<int> request=desired=>{
                long generation=inspection.Advance();side=desired;feedback.Text=image.Source==null ? "Loading source preview…" : "Showing cached preview while loading source detail…";
                if(!loader.QueueInspect(row,generation,desired))feedback.Text="Source preview is unavailable during shutdown.";
            };
            Action apply=()=>{
                image.LayoutTransform=rotation==0 ? Transform.Identity : new RotateTransform(rotation);
                image.Width=image.Height=Double.NaN;
                preview.HorizontalScrollBarVisibility=preview.VerticalScrollBarVisibility=layout=="Zoom" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
                image.Stretch=layout=="Fill" ? Stretch.UniformToFill : Stretch.Uniform;
                if(layout!="Zoom" || sourceWidth<1)return;
                image.Width=Math.Max(1,sourceWidth*zoom);image.Height=Math.Max(1,sourceHeight*zoom);
            };
            Action<int> rotate=delta=>{
                rotation=((rotation+delta)%360+360)%360;
                apply();
            };
            var refine=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};
            refine.Tick+=(s,e)=>{refine.Stop();Guard(()=>{
                if(live && !video && sourceWidth>0){var plan=InspectionPlan.Create(sourceWidth,sourceHeight,Math.Max(1,preview.ViewportWidth),Math.Max(1,preview.ViewportHeight),layout,zoom);if(plan.DecodeSide>side)request(plan.DecodeSide);}
            });};
            Action schedule=()=>{refine.Stop();refine.Start();};
            Action<string,double> choose=(mode,scale)=>{
                layout=mode;zoom=Math.Min(8,Math.Max(0.05,scale));apply();schedule();
            };
            preview.SizeChanged+=(s,e)=>schedule();
            preview.PreviewMouseWheel+=(s,e)=>{
                if(sourceWidth==0 || sourceHeight==0)return;
                Point point=e.GetPosition(preview);
                var plan=InspectionPlan.Create(sourceWidth,sourceHeight,Math.Max(1,preview.ViewportWidth),Math.Max(1,preview.ViewportHeight),layout,zoom);
                double x=(preview.HorizontalOffset+point.X-Math.Max(0,(preview.ViewportWidth-plan.DisplayWidth)/2))/plan.Scale;
                double y=(preview.VerticalOffset+point.Y-Math.Max(0,(preview.ViewportHeight-plan.DisplayHeight)/2))/plan.Scale;
                choose("Zoom",plan.Scale*(e.Delta>0 ? 1.25 : 0.8));
                preview.UpdateLayout();preview.ScrollToHorizontalOffset(x*zoom-point.X);preview.ScrollToVerticalOffset(y*zoom-point.Y);e.Handled=true;
            };
            Point? drag=null;double dragX=0,dragY=0;
            image.MouseLeftButtonDown+=(s,e)=>{if(layout!="Zoom")return;drag=e.GetPosition(preview);dragX=preview.HorizontalOffset;dragY=preview.VerticalOffset;image.CaptureMouse();image.Cursor=Cursors.Hand;e.Handled=true;};
            image.MouseMove+=(s,e)=>{if(!drag.HasValue || e.LeftButton!=MouseButtonState.Pressed)return;Vector move=e.GetPosition(preview)-drag.Value;preview.ScrollToHorizontalOffset(dragX-move.X);preview.ScrollToVerticalOffset(dragY-move.Y);};
            image.MouseLeftButtonUp+=(s,e)=>image.ReleaseMouseCapture();
            image.LostMouseCapture+=(s,e)=>{drag=null;image.Cursor=null;};
            actions.Children.Add(ActionButton("Fit",()=>Guard(()=>choose("Fit",1))));
            actions.Children.Add(ActionButton("Fill",()=>Guard(()=>choose("Fill",1))));
            actions.Children.Add(ActionButton("−",()=>Guard(()=>choose("Zoom",zoom/1.25))));
            actions.Children.Add(ActionButton("+",()=>Guard(()=>choose("Zoom",zoom*1.25))));
            actions.Children.Add(ActionButton("100%",()=>Guard(()=>choose("Zoom",1))));
            if(!video){
                actions.Children.Add(ActionButton("↶ 90°",()=>Guard(()=>rotate(-90))));
                actions.Children.Add(ActionButton("↷ 90°",()=>Guard(()=>rotate(90))));
            }
            actions.Children.Add(ActionButton("Retry",()=>Guard(()=>request(Math.Max(256,side)))));
            actions.Children.Add(ActionButton("Details / notes",()=>Guard(()=>Details(row))));
            VideoPlaybackControls player=null;
            Button fullButton=null;
            Action updateFullScreen=()=>{
                bool full=inspectFullScreen.Active;
                fullButton.Content=full ? "Exit full screen" : "Full screen";
                panel.Margin=full ? new Thickness(6) : new Thickness(16);
                if(player!=null)player.SetFullScreenState(full);
            };
            Action toggleFullScreen=()=>{
                inspectFullScreen.Set(!inspectFullScreen.Active);updateFullScreen();viewport.Focus();
            };
            fullButton=ActionButton("Full screen",()=>Guard(toggleFullScreen));
            fullButton.ToolTip="Full screen (F or F11). Press Esc to exit.";
            actions.Children.Add(fullButton);
            if(video){
                player=CreateVideoPlayer(viewport,row);
                player.AttachPoster(image);
                player.ToggleFullScreen=toggleFullScreen;
                player.AttachHoverHost(mediaStage,viewport);
                player.VerticalAlignment=VerticalAlignment.Bottom;
                mediaStage.Children.Add(player);
            }
            updateFullScreen();
            if(position>0)actions.Children.Add(ActionButton("← Previous item",()=>Guard(()=>navigate(-1))));
            if(position>=0 && position+1<cards.Count)actions.Children.Add(ActionButton("Next item →",()=>Guard(()=>navigate(1))));
            top.Children.Insert(2,Label("Arrow keys: ← Previous · → Next · ↑ Compliant · ↓ Non-compliant · R Rotate"));
            KeyEventHandler keys=(s,e)=>{
                if(!Ready || Keyboard.Modifiers!=ModifierKeys.None)return;
                Key key=e.Key;
                if(key==Key.Escape && inspectFullScreen.Active){
                    e.Handled=true;if(!e.IsRepeat)Guard(toggleFullScreen);return;
                }
                if(reviewer.IsKeyboardFocusWithin || notes.IsKeyboardFocusWithin)return;
                if(key==Key.F || key==Key.F11){
                    e.Handled=true;if(!e.IsRepeat)Guard(toggleFullScreen);return;
                }
                if(player!=null && viewport.IsKeyboardFocusWithin && key==Key.Space){
                    e.Handled=true;if(!e.IsRepeat)player.TogglePlay();return;
                }
                if(player!=null && player.IsKeyboardFocusWithin)return;
                if(!video && key==Key.R && !e.IsRepeat){e.Handled=true;Guard(()=>rotate(90));return;}
                string destination=QuickReviewKey(key);
                if(key!=Key.Left && key!=Key.Right && destination==null)return;
                e.Handled=true;
                if(e.IsRepeat)return;
                if(destination!=null)Guard(()=>classify(destination));
                else Guard(()=>navigate(key==Key.Left ? -1 : 1));
            };
            window.PreviewKeyDown+=keys;
            showInspection=result=>{
                if(result.Image!=null){
                    image.Source=result.Image;sourceWidth=result.SourceWidth;sourceHeight=result.SourceHeight;
                    apply();
                    string detail=result.Image.PixelWidth==sourceWidth && result.Image.PixelHeight==sourceHeight ? "" :
                        " · Preview "+result.Image.PixelWidth+" × "+result.Image.PixelHeight;
                    feedback.Text=sourceWidth+" × "+sourceHeight+detail+" · "+row.RelativePath;
                }
                else feedback.Text=result.Error??"Preview unavailable.";
            };
            clearInspector=()=>{
                live=false;window.PreviewKeyDown-=keys;refine.Stop();
                loader.CancelInspect(inspection.Advance());showInspection=null;image.Source=null;
                if(player!=null)player.Dispose();
            };
            viewport.Loaded+=(s,e)=>{
                if(!live)return;
                if(!video)Guard(()=>request(Math.Min(InspectionPlan.MaxSide,Math.Max(256,(int)Math.Ceiling(Math.Max(preview.ActualWidth,preview.ActualHeight))))));
                else{
                    if(image.Source==null && (row.PreviewBytes!=null || CanPlayQuickVideo(row)))
                        Guard(()=>loader.QueueInspect(row,inspection.Advance(),256,true));
                    // As in Quick Review, the video is opened only when the reviewer presses Play.
                }
                viewport.Focus();
            };
            if(!opened)window.Show();
        }

        VideoPlaybackControls CreateVideoPlayer(Grid viewport,MediaRow row) {
            bool proxy=Formats.UsesPlaybackProxy(row.Extension);
            ReviewerEngine sourceEngine=null;
            string request=null;
            return new VideoPlaybackControls(viewport,complete=>{
                if(!Ready)throw new InvalidOperationException("The catalog is unavailable.");
                var catalog=engine;
                string id=catalog.RequestVideoPlayback(row);
                sourceEngine=catalog;request=id;
                Queue(id,result=>complete(result.Value,result.Success ? null : result.Error));
                return ()=>{pending.Remove(id);catalog.CancelVideoPlayback(id);};
            },()=>sourceEngine==null ? null : sourceEngine.VideoStage(request)) {TemporarySource=proxy,Loop=row.Extension=="gif"};
        }
        int positionOf(MediaRow row){return cards.ToList().FindIndex(c=>c.Record.GroupId==row.GroupId && c.Record.ContentId==row.ContentId);}

    }
}
