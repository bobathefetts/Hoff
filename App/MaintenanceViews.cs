using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MediaReviewer {
    sealed partial class ReviewerWindow {
        static ListView ResultsList(params GridViewColumn[] columns) {
            var view=new GridView();foreach(var column in columns)view.Columns.Add(column);
            return new ListView {View=view};
        }
        static GridViewColumn Column(string label,string property,double width){return new GridViewColumn {Header=label,Width=width,DisplayMemberBinding=new Binding(property)};}

        void ManageSources() {
            var layout=new DockPanel {Margin=new Thickness(16)};var dialog=Dialog("Sources / reconnect",layout,850,620);
            var actions=Bar();Dock(layout,actions,System.Windows.Controls.Dock.Bottom);
            var feedback=Label("Loading saved sources…");Dock(layout,feedback,System.Windows.Controls.Dock.Bottom);
            var header=new StackPanel();Dock(layout,header,System.Windows.Controls.Dock.Top);
            header.Children.Add(Label("Select a recorded root and check its replacement folder before applying a connection."));
            var roots=new ComboBox {DisplayMemberPath="Label",MinHeight=32,Margin=new Thickness(3)};header.Children.Add(roots);
            var pathBar=new DockPanel();header.Children.Add(pathBar);
            var path=Editor("",32767);
            Dock(pathBar,ActionButton("Browse…",()=>Guard(()=>{string chosen=ChooseFolder("Choose a replacement media root",path.Text);if(chosen!=null)path.Text=chosen;})),System.Windows.Controls.Dock.Right);
            pathBar.Children.Add(path);
            var counts=Label("");header.Children.Add(counts);
            var results=ResultsList(Column("Relative path","Path",330),Column("Result","State",120),Column("Details","Error",320));layout.Children.Add(results);

            var owner=engine;var cursors=new Stack<string>();string after="",next="";bool live=true,busy=false,loading=false;
            SourceConnection current=null;
            var loadVersion=new ViewGeneration();
            Func<SourceRoot> selected=()=>roots.SelectedItem as SourceRoot;
            Action refreshButtons=null,loadRoots=null;
            Action<string> loadStatus=id=>{
                long version=loadVersion.Advance();current=null;results.ItemsSource=null;counts.Text="";refreshButtons();
                if(String.IsNullOrEmpty(id))return;
                Queue(owner.RequestSourceConnectionStatus(id),r=>{
                    if(!live || !loadVersion.Accept(version))return;
                    if(!r.Success){feedback.Text=r.Error;refreshButtons();return;}
                    current=r.SourceConnection;results.ItemsSource=current.Rows;
                    counts.Text=String.Format("{0} · {1:N0} full matches · {2:N0} candidates{6} · {3:N0} missing · {4:N0} failed\n{5}",current.State,current.Full,current.Candidates,current.Missing,current.Failed,current.Path,
                        current.Unaccepted>0 ? String.Format(" ({0:N0} not accepted)",current.Unaccepted) : "");
                    refreshButtons();
                });
            };
            Action<string,string,string> run=(request,label,id)=>{
                busy=true;refreshButtons();feedback.Text=label+" queued…";
                Work(request,label,r=>{
                    if(!live)return;
                    busy=false;feedback.Text=r.Success ? r.Value??label+" complete." : r.Error;
                    if(id==null)loadRoots();else loadStatus(id);
                    refreshButtons();
                },true);
            };
            var previous=ActionButton("Previous roots",()=>Guard(()=>{after=cursors.Pop();loadRoots();}));
            var more=ActionButton("Next roots",()=>Guard(()=>{cursors.Push(after);after=next;loadRoots();}));
            var reload=ActionButton("Refresh",()=>Guard(()=>loadRoots()));
            var check=ActionButton("Check folder",()=>Guard(()=>{
                string id=owner.RequestRootRemap(selected().Id,path.Text.Trim(),false);run(id,"Check source",id);
            }));
            var resume=ActionButton("Resume check",()=>Guard(()=>{string id=current.Id;run(owner.RequestResumeSourceConnection(id),"Resume source check",id);}));
            var apply=ActionButton("Apply connection",()=>Guard(()=>{
                bool acknowledge=current.Candidates>0 || current.Missing>0;
                string prompt=acknowledge ? "Apply this connection with the listed missing files and quick candidates? A quick candidate is not full-file proof." : "Apply this verified source connection?";
                if(MessageBox.Show(dialog,prompt,"Apply source connection",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                run(owner.RequestApplySourceConnection(current.Id,acknowledge),"Apply source connection",null);
            }));
            var discard=ActionButton("Discard check",()=>Guard(()=>{
                if(MessageBox.Show(dialog,"Discard this unapplied source check?","Discard source check",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
                    run(owner.RequestDiscardSourceConnection(current.Id),"Discard source check",null);
            }));
            refreshButtons=()=>{
                bool idle=live && ReferenceEquals(owner,engine) && Ready && !busy && !loading;
                bool operations=idle && (status==null || !status.RunningScan);
                bool hasCheck=current!=null && new[]{"checking","interrupted","ready","discarding"}.Contains(current.State);
                roots.IsEnabled=reload.IsEnabled=idle;
                previous.IsEnabled=idle && cursors.Count>0;more.IsEnabled=idle && !String.IsNullOrEmpty(next);
                check.IsEnabled=operations && selected()!=null && !hasCheck && !String.IsNullOrWhiteSpace(path.Text);
                resume.IsEnabled=operations && hasCheck && (current.State=="checking" || current.State=="interrupted");
                apply.IsEnabled=operations && hasCheck && current.State=="ready" && current.Full+current.Candidates>0 && current.Failed==0;
                discard.IsEnabled=operations && hasCheck;
            };
            loadRoots=()=>{
                string id=selected()==null ? null : selected().Id;loading=true;refreshButtons();
                Queue(owner.RequestSourceRoots(after),r=>{
                    if(!live)return;loading=false;
                    if(!r.Success){feedback.Text=r.Error;refreshButtons();return;}
                    next=r.SourceRoots.HasMore ? r.SourceRoots.Next : "";
                    roots.ItemsSource=r.SourceRoots.Rows;
                    roots.SelectedItem=r.SourceRoots.Rows.FirstOrDefault(root=>root.Id==id)??r.SourceRoots.Rows.FirstOrDefault();
                    if(selected()==null)feedback.Text="No source roots have been recorded.";
                    refreshButtons();
                });
            };
            roots.SelectionChanged+=(s,e)=>Guard(()=>{
                var root=selected();path.Text=root==null ? "" : root.Path;
                feedback.Text=root==null ? "" : "Recorded root · "+root.RecordedPath;
                loadStatus(root==null ? null : root.ConnectionId);
            });
            path.TextChanged+=(s,e)=>refreshButtons();
            foreach(var button in new[]{previous,more,reload,check,resume,apply,discard,ActionButton("Close",dialog.Close)})actions.Children.Add(button);
            var controlsTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
            controlsTimer.Tick+=(s,e)=>refreshButtons();
            dialog.Closed+=(s,e)=>{live=false;controlsTimer.Stop();};TrackDatabaseWindow(dialog);dialog.Show();
            controlsTimer.Start();loadRoots();
        }

        void PreviewTasks() {
            var layout=new DockPanel {Margin=new Thickness(16)};var dialog=Dialog("Preview tasks",layout,800,560);
            var tasks=new ComboBox {DisplayMemberPath="Label",MinHeight=32,Margin=new Thickness(3)};Dock(layout,tasks,System.Windows.Controls.Dock.Top);
            var actions=Bar();Dock(layout,actions,System.Windows.Controls.Dock.Bottom);
            var feedback=Label("Loading preview tasks…");Dock(layout,feedback,System.Windows.Controls.Dock.Bottom);
            var results=ResultsList(Column("Group","GroupId",85),Column("State","State",130),Column("Details","Error",510));layout.Children.Add(results);
            var owner=engine;var generation=new ViewGeneration();bool live=true,busy=false;
            Action load=null,update=null;
            Func<PreviewRetryBatch> selection=()=>tasks.SelectedItem as PreviewRetryBatch;
            Action<string,string> run=(id,label)=>{
                busy=true;update();
                Work(id,label,r=>{if(!live)return;busy=false;feedback.Text=r.Success ? r.Value : r.Error;load();},true);
            };
            var resume=ActionButton("Resume",()=>Guard(()=>run(owner.RequestResumePreviewRetry(selection().Id),"Resume preview task")));
            var discard=ActionButton("Discard",()=>Guard(()=>{
                if(MessageBox.Show(dialog,"Discard pending work from this preview task? Saved previews are kept.","Discard preview task",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
                    run(owner.RequestDiscardPreviewRetry(selection().Id),"Discard preview task");
            }));
            var refresh=ActionButton("Refresh",()=>Guard(load));
            update=()=>{
                var selected=selection();
                bool actionable=live && !busy && selected!=null && (selected.State=="queued" || selected.State=="interrupted");
                resume.IsEnabled=discard.IsEnabled=actionable;
                refresh.IsEnabled=tasks.IsEnabled=!busy;
            };
            load=()=>{
                string previous=selection()==null ? null : selection().Id;long version=generation.Advance();
                Queue(owner.RequestPreviewRetryHistory(),r=>{
                    if(!live || !generation.Accept(version))return;
                    if(!r.Success){feedback.Text=r.Error;return;}
                    tasks.ItemsSource=r.PreviewRetries;
                    tasks.SelectedItem=r.PreviewRetries.FirstOrDefault(batch=>batch.Id==previous)??r.PreviewRetries.FirstOrDefault();
                    if(selection()==null)feedback.Text="No preview tasks recorded.";
                    update();
                });
            };
            tasks.SelectionChanged+=(s,e)=>Guard(()=>{
                var selected=selection();long version=generation.Advance();results.ItemsSource=null;update();if(selected==null)return;
                feedback.Text=selected.Label;
                Queue(owner.RequestPreviewRetryStatus(selected.Id),r=>{
                    if(!live || !generation.Accept(version))return;
                    if(!r.Success){feedback.Text=r.Error;return;}
                    results.ItemsSource=r.PreviewRetry.Items;feedback.Text=r.PreviewRetry.Label;
                });
            });
            foreach(var button in new[]{resume,discard,refresh,ActionButton("Close",dialog.Close)})actions.Children.Add(button);
            dialog.Closed+=(s,e)=>live=false;TrackDatabaseWindow(dialog);dialog.Show();update();load();
        }

        void IdentityResults(IdentityResolutionPreview preview) {
            var layout=new DockPanel {Margin=new Thickness(16)};var dialog=Dialog("Verified identity results",layout,900,630);
            var header=Label(preview.Message+"\n\nCurrent review · "+preview.SourceStatus+" · "+preview.SourceReviewer+"\n"+preview.SourceNotes);
            header.MaxHeight=140;Dock(layout,new ScrollViewer {Content=header,MaxHeight=140},System.Windows.Controls.Dock.Top);
            var actions=Bar();Dock(layout,actions,System.Windows.Controls.Dock.Bottom);
            var confirm=new CheckBox {Content="I have reviewed the differing decisions and accept the listed conflict outcomes.",Margin=new Thickness(4),Visibility=preview.HasConflicts ? Visibility.Visible : Visibility.Collapsed};
            Dock(layout,confirm,System.Windows.Controls.Dock.Bottom);
            var results=ResultsList(Column("Full SHA-256","Hash",190),Column("Locations","Locations",70),Column("Decision","Status",100),Column("Reviewer","Reviewer",100),Column("Notes","Notes",165),Column("Conflict","Conflict",60),Column("Example location","ExamplePath",220));
            results.ItemsSource=preview.Outcomes;layout.Children.Add(results);
            bool applied=false;
            var owner=engine;
            var apply=ActionButton("Apply verified identities",()=>Guard(()=>{
                preview.AcceptConflicts=confirm.IsChecked==true;
                applied=true;
                Work(owner.RequestIdentityResolution(preview,Id()),"Apply verified identities");dialog.Close();
            }));
            apply.IsEnabled=preview.CanApply && !preview.HasConflicts;
            confirm.Checked+=(s,e)=>apply.IsEnabled=preview.CanApply;
            confirm.Unchecked+=(s,e)=>apply.IsEnabled=false;
            dialog.Closed+=(s,e)=>{
                if(!applied && ReferenceEquals(owner,engine) && Ready && !String.IsNullOrEmpty(preview.RunId)){
                    try{owner.RequestDiscardIdentityRun(preview.RunId);}catch{}
                }
            };
            actions.Children.Add(apply);actions.Children.Add(ActionButton("Close",dialog.Close));
            TrackDatabaseWindow(dialog);dialog.Show();
        }

        void Report() {
            var body=new StackPanel {Margin=new Thickness(16)};var dialog=Dialog("Export review report",body,550,390);
            body.Children.Add(Label("Reports contain saved non-compliant decisions and recorded locations. Choose a new output file or folder."));
            var formats=new ComboBox {MinHeight=32};formats.Items.Add("CSV spreadsheet");formats.Items.Add("Offline HTML folder with CSV");formats.SelectedIndex=0;body.Children.Add(formats);
            var previews=new CheckBox {Content="Include saved previews in the HTML report",IsChecked=true,IsEnabled=false};body.Children.Add(previews);
            formats.SelectionChanged+=(s,e)=>previews.IsEnabled=formats.SelectedIndex==1;
            var safe=new CheckBox {Content="Protect spreadsheet text from formulas",IsChecked=true};body.Children.Add(safe);
            var evidence=new CheckBox {Content="Include authenticated Evidence details in plaintext"};body.Children.Add(evidence);
            body.Children.Add(Label("Reports are readable without a password. Export a protected Evidence package separately to share encrypted originals."));
            var actions=Bar();body.Children.Add(actions);
            var proceed=ActionButton("Choose destination…",()=>dialog.DialogResult=true);proceed.IsDefault=true;actions.Children.Add(proceed);
            actions.Children.Add(ActionButton("Cancel",dialog.Close));
            if(dialog.ShowDialog()!=true)return;
            bool csv=formats.SelectedIndex==0;
            string destination=SaveFile(csv ? "CSV file (*.csv)|*.csv" : "New report folder (*.*)|*.*",csv ? "Media Review.csv" : "Media Review Report");
            if(destination==null)return;
            var report=new ReportOptions {Destination=destination,Format=csv ? "Csv" : "HtmlDirectory",IncludeSavedPreviews=!csv && previews.IsChecked==true,SpreadsheetSafeText=safe.IsChecked==true,IncludeEvidence=evidence.IsChecked==true};
            Action export=()=>Work(engine.RequestReport(report),"Export report");
            if(report.IncludeEvidence && !engine.VaultUnlocked)Unlock(export);else export();
        }
    }
}
