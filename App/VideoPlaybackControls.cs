using System;
using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaReviewer {
    // One player belongs to one inspection or Quick Review stage. Hovering the video reveals the streaming HUD.
    sealed class VideoPlaybackControls:Border,IDisposable {
        static readonly DependencyPropertyDescriptor PosterSource=DependencyPropertyDescriptor.FromProperty(Image.SourceProperty,typeof(Image));
        static readonly double[] Speeds={1.0,1.25,1.5,2.0};
        static readonly Brush OverlayBack=FrozenBrush(130,12,15,22);
        static readonly Brush OverlayBorder=FrozenBrush(70,255,255,255);
        static readonly Brush OverlayText=FrozenBrush(255,248,250,252);
        static readonly Brush OverlayMuted=FrozenBrush(215,215,225,235);
        static readonly Brush ButtonBack=FrozenBrush(115,28,34,46);
        static readonly Brush ButtonBorder=FrozenBrush(85,255,255,255);
        static readonly Brush CenterBadgeBack=FrozenBrush(145,12,15,22);
        static readonly Brush CenterBadgeBorder=FrozenBrush(140,255,255,255);
        readonly Grid viewport;
        readonly Func<Action<string,string>,Action> resolve;
        readonly Func<string> preparationStage;
        readonly Button play,stop,back,forward,mute,speed,fullScreen;
        readonly Slider seek,volume;
        readonly TextBlock time,status;
        readonly Border centerPlayBadge;
        readonly TextBlock centerPlayIcon;
        readonly DispatcherTimer timer,hideTimer;
        readonly MouseEventHandler onHostEnter,onHostMove,onHostLeave;
        readonly MouseButtonEventHandler onHostClick;
        MediaElement media;
        Image poster;
        UIElement hoverHost,focusTarget;
        internal bool TemporarySource,Loop;
        internal Action ToggleFullScreen;
        string playbackPath;
        Action cancelResolution;
        bool disposed,opened,paused,dragging,updating,ended,preparing,playRequested,userStopped;
        long generation;
        double duration,speedRatio=1.0;
        DateTime lastDragSeekUtc=DateTime.MinValue;
        Point lastMousePos;

        static SolidColorBrush FrozenBrush(byte a,byte r,byte g,byte b) {
            var brush=new SolidColorBrush(Color.FromArgb(a,r,g,b));
            brush.Freeze();return brush;
        }

        internal VideoPlaybackControls(Grid viewport,Func<Action<string,string>,Action> resolve,Func<string> preparationStage=null) {
            this.viewport=viewport;this.resolve=resolve;this.preparationStage=preparationStage;
            focusTarget=viewport;
            Focusable=false;
            Margin=new Thickness(0,6,0,0);Padding=new Thickness(14,9,14,9);CornerRadius=new CornerRadius(14);
            Background=OverlayBack;BorderBrush=OverlayBorder;BorderThickness=new Thickness(1);
            Opacity=1;
            var panel=new StackPanel {Focusable=false};Child=panel;
            status=new TextBlock {Text="Video · click screen to pause or play.",Foreground=OverlayMuted,FontSize=11.5,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,0,3,3),Focusable=false};
            panel.Children.Add(status);
            var timeline=new DockPanel {Focusable=false};panel.Children.Add(timeline);
            time=new TextBlock {Text="0:00 / --:--",Foreground=OverlayText,FontWeight=FontWeights.SemiBold,MinWidth=110,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,3,0),Focusable=false};
            DockPanel.SetDock(time,Dock.Right);timeline.Children.Add(time);
            seek=new Slider {Minimum=0,Maximum=1,IsEnabled=false,IsMoveToPointEnabled=true,SmallChange=1,LargeChange=10,Margin=new Thickness(4,4,6,4),VerticalAlignment=VerticalAlignment.Center,Cursor=Cursors.Hand,ToolTip="Click or drag to seek. Use the arrow keys when the seek bar is focused."};
            AutomationProperties.SetName(seek,"Video position in seconds");timeline.Children.Add(seek);
            var buttons=new WrapPanel {Focusable=false};panel.Children.Add(buttons);
            play=MakeButton("Play",Toggle,"Play or pause video (click video screen or press Space)");
            stop=MakeButton("Stop",()=>{userStopped=true;Reset();status.Text="Stopped · click screen or Play to restart.";UpdateCenterBadge();ShowOverlay();},"Stop playback and release the source file");
            back=MakeButton("−10 s",()=>SeekTo(seek.Value-10),"Skip back ten seconds");
            forward=MakeButton("+10 s",()=>SeekTo(seek.Value+10),"Skip forward ten seconds");
            mute=MakeButton("Mute",()=>{if(media!=null)media.IsMuted=!media.IsMuted;else muted=!muted;UpdateMute();},"Mute or restore video audio");
            foreach(var button in new[]{play,stop,back,forward,mute})buttons.Children.Add(button);
            buttons.Children.Add(new TextBlock {Text="Volume",Foreground=OverlayMuted,Margin=new Thickness(10,0,4,0),VerticalAlignment=VerticalAlignment.Center,Focusable=false});
            volume=new Slider {Minimum=0,Maximum=1,Value=0.5,IsMoveToPointEnabled=true,SmallChange=0.05,LargeChange=0.1,Width=100,Margin=new Thickness(5),VerticalAlignment=VerticalAlignment.Center,Cursor=Cursors.Hand,ToolTip="Playback volume"};
            AutomationProperties.SetName(volume,"Video volume");buttons.Children.Add(volume);
            speed=MakeButton("1×",CycleSpeed,"Cycle playback speed (1×, 1.25×, 1.5×, 2×)");
            buttons.Children.Add(speed);
            fullScreen=MakeButton("⛶",()=>{if(ToggleFullScreen!=null)ToggleFullScreen();},"Toggle full screen (F / Esc)");
            buttons.Children.Add(fullScreen);

            centerPlayIcon=new TextBlock {
                Text="▶",Foreground=OverlayText,FontSize=28,FontWeight=FontWeights.Bold,
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
                Margin=new Thickness(4,0,0,0),Focusable=false,IsHitTestVisible=false
            };
            centerPlayBadge=new Border {
                Width=68,Height=68,CornerRadius=new CornerRadius(34),
                Background=CenterBadgeBack,BorderBrush=CenterBadgeBorder,BorderThickness=new Thickness(1.5),
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
                Focusable=false,IsHitTestVisible=false,Visibility=Visibility.Collapsed,
                Child=centerPlayIcon
            };
            Panel.SetZIndex(centerPlayBadge,9);

            onHostEnter=(s,e)=>ShowOverlay();
            onHostMove=(s,e)=>{
                var el=s as IInputElement;
                if(el==null)return;
                Point pos=e.GetPosition(el);
                if((pos-lastMousePos).Length>=1.0){lastMousePos=pos;ShowOverlay();}
            };
            onHostLeave=(s,e)=>CheckMouseLeft();
            onHostClick=(s,e)=>{
                if(IsDescendantOf(e.OriginalSource as DependencyObject,this))return;
                e.Handled=true;
                RestoreContentFocus();
                TogglePlay();
            };

            volume.ValueChanged+=(s,e)=>{if(media!=null)media.Volume=volume.Value;ShowOverlay();};
            seek.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((s,e)=>{dragging=true;ShowOverlay();}));
            seek.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((s,e)=>{dragging=false;if(!e.Canceled)SeekTo(seek.Value);ShowOverlay();}));
            seek.ValueChanged+=(s,e)=>{
                if(updating)return;
                ShowOverlay();
                if(!dragging){SeekTo(seek.Value);return;}
                UpdateTime(seek.Value);
                if(opened && duration>0 && DateTime.UtcNow-lastDragSeekUtc>=TimeSpan.FromMilliseconds(90)){
                    lastDragSeekUtc=DateTime.UtcNow;
                    Safe(()=>{media.Position=TimeSpan.FromSeconds(Math.Max(0,Math.Min(duration,seek.Value)));ended=false;});
                }
            };
            IsKeyboardFocusWithinChanged+=(s,e)=>ShowOverlay();
            PreviewKeyDown+=(s,e)=>{ShowOverlay();if(e.Key==Key.Space && !e.IsRepeat && !(e.OriginalSource is Button) && Keyboard.Modifiers==ModifierKeys.None){e.Handled=true;Toggle();}};
            MouseEnter+=(s,e)=>ShowOverlay();
            PreviewMouseMove+=(s,e)=>ShowOverlay();
            MouseLeave+=(s,e)=>CheckMouseLeft();
            timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(100)};
            timer.Tick+=Tick;
            hideTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(1800)};
            hideTimer.Tick+=(s,e)=>{
                hideTimer.Stop();
                if(!dragging && !IsMouseOver)HideOverlay();
            };
            AttachHoverHost(viewport,viewport);
            Loaded+=(s,e)=>{if(IsMouseOver || (hoverHost!=null && hoverHost.IsMouseOver))ShowOverlay();};
            SetEnabled(false);
        }

        internal void AttachPoster(Image image) {
            poster=image;PosterSource.AddValueChanged(poster,PosterChanged);UpdateCenterBadge();
        }
        void PosterChanged(object sender,EventArgs args){UpdateCenterBadge();}

        internal void AttachHoverHost(UIElement host,UIElement focusElement=null) {
            if(host==null)return;
            DetachHoverHost();
            hoverHost=host;
            if(focusElement!=null)focusTarget=focusElement;
            var stageGrid=host as Grid;
            if(stageGrid!=null && !ReferenceEquals(stageGrid,viewport)){
                if(!stageGrid.Children.Contains(centerPlayBadge))stageGrid.Children.Add(centerPlayBadge);
                if(ReferenceEquals(focusTarget,viewport))stageGrid.MouseLeftButtonDown+=onHostClick;
            }
            host.MouseEnter+=onHostEnter;
            host.PreviewMouseMove+=onHostMove;
            host.MouseLeave+=onHostLeave;
            UpdateCenterBadge();
        }
        void DetachHoverHost() {
            if(hoverHost==null)return;
            hoverHost.MouseEnter-=onHostEnter;
            hoverHost.PreviewMouseMove-=onHostMove;
            hoverHost.MouseLeave-=onHostLeave;
            var stageGrid=hoverHost as Grid;
            if(stageGrid!=null){
                stageGrid.MouseLeftButtonDown-=onHostClick;
                stageGrid.Children.Remove(centerPlayBadge);
            }
            hoverHost=null;
        }
        static bool IsDescendantOf(DependencyObject node,DependencyObject ancestor) {
            for(var cur=node;cur!=null;cur=cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
                if(ReferenceEquals(cur,ancestor))return true;
            return false;
        }
        void RestoreContentFocus() {
            var target=focusTarget??viewport;
            if(target!=null && target.Focusable && !target.IsKeyboardFocused){
                Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{if(!disposed)target.Focus();}));
            }
        }
        void UpdateCenterBadge() {
            bool hasFrame=media!=null && media.Opacity==1;
            bool missingPicture=!hasFrame && (poster==null || poster.Source==null);
            // Before the video is opened (it opens only on Play) the badge shows it is a video, not a still picture.
            bool showPlay=!preparing && !playRequested && ((opened && (paused || ended)) || !opened);
            centerPlayBadge.Visibility=!disposed && (missingPicture || showPlay) ? Visibility.Visible : Visibility.Collapsed;
            centerPlayBadge.Width=missingPicture ? Double.NaN : 68;
            centerPlayBadge.Height=missingPicture ? Double.NaN : 68;
            centerPlayBadge.Padding=missingPicture ? new Thickness(18,10,18,10) : new Thickness(0);
            centerPlayIcon.FontSize=missingPicture ? 15 : 28;
            centerPlayIcon.Text=missingPicture ? PictureStatus() : ended ? "↻" : "▶";
        }
        string PictureStatus(){
            if(preparing || opened && !paused && media.NaturalVideoWidth>0)return "Loading video…";
            if(opened && media.NaturalVideoWidth==0)return "No video picture";
            if(userStopped)return "Press Play to try again";
            return "Press Play";
        }
        void ShowOverlay() {
            if(disposed)return;
            Opacity=1.0;IsHitTestVisible=true;
            hideTimer.Stop();
            hideTimer.Start();
        }
        void HideOverlay() {
            if(disposed || dragging || !opened || paused || IsKeyboardFocusWithin)return;
            hideTimer.Stop();
            Opacity=0.0;IsHitTestVisible=false;
        }
        void CheckMouseLeft() {
            if(dragging || IsMouseOver)return;
            if(hoverHost!=null && hoverHost.IsMouseOver)return;
            if(viewport!=null && viewport.IsMouseOver)return;
            HideOverlay();
        }

        bool muted;
        Button MakeButton(string label,Action action,string hint) {
            var button=new Button {
                Content=label,ToolTip=hint,MinWidth=52,MinHeight=26,Padding=new Thickness(10,3,10,3),Margin=new Thickness(2,2,2,2),
                Background=ButtonBack,Foreground=OverlayText,BorderBrush=ButtonBorder,BorderThickness=new Thickness(1),
                Cursor=Cursors.Hand
            };
            button.Click+=(s,e)=>{ShowOverlay();Safe(action);};
            return button;
        }
        void Safe(Action action) {if(disposed)return;try{action();}catch(Exception error){Fail("Playback unavailable: "+error.GetBaseException().Message);}}
        internal void SetFullScreenState(bool isFull) {
            fullScreen.Content=isFull ? "✕ Esc" : "⛶";
            AutomationProperties.SetName(fullScreen,isFull ? "Exit full screen" : "Full screen");
        }
        internal void TogglePlay() {Toggle();RestoreContentFocus();}
        void CycleSpeed() {
            int idx=Array.IndexOf(Speeds,speedRatio);
            speedRatio=Speeds[(idx+1)%Speeds.Length];
            speed.Content=speedRatio.ToString("0.##",CultureInfo.InvariantCulture)+"×";
            if(media!=null && opened)Safe(()=>{media.SpeedRatio=speedRatio;});
        }
        void Toggle() {
            if(disposed)return;
            userStopped=false;
            if(opened){
                Safe(()=>{
                    if(paused || ended){
                        if(ended){media.Position=TimeSpan.Zero;ended=false;}
                        media.SpeedRatio=speedRatio;media.Play();paused=false;playRequested=false;
                    }else{
                        media.Pause();paused=true;playRequested=false;
                    }
                    play.Content=paused ? "Play" : "Pause";
                    status.Text=paused ? "Paused · click screen to play" : (speedRatio==1.0 ? "Playing" : "Playing ("+speed.Content+")");
                    UpdateCenterBadge();
                    if(paused)ShowOverlay();
                });
                return;
            }
            if(preparing){playRequested=true;play.IsEnabled=false;status.Text="Starting playback…";UpdateCenterBadge();return;}
            BeginPrepare(true);
        }
        internal void Prepare() {if(!disposed && !userStopped && !opened && !preparing)BeginPrepare(false);}
        // True once this player has asked for its video, whether or not that succeeded. The self-test uses it.
        internal bool LoadRequested {get;private set;}
        void BeginPrepare(bool startPlaying) {
            playRequested=startPlaying;preparing=true;LoadRequested=true;
            long request=++generation;play.IsEnabled=!startPlaying;stop.IsEnabled=true;
            status.Text=TemporarySource ? "Preparing offline playback copy…" : "Loading video…";
            timer.Start();ShowOverlay();
            UpdateCenterBadge();
            Safe(()=>{
                bool resolved=false;
                Action cancel=resolve((path,error)=>{
                    resolved=true;
                    if(disposed || request!=generation)return;
                    cancelResolution=null;
                    if(error!=null){Fail(error);return;}
                    Safe(()=>Open(path));
                });
                if(!resolved && request==generation)cancelResolution=cancel;
            });
        }
        void Open(string path) {
            playbackPath=path;
            var player=new MediaElement {LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform,Volume=volume.Value,IsMuted=muted,Cursor=Cursors.Hand,Focusable=false,Opacity=0};
            player.PreviewMouseMove+=(s,e)=>ShowOverlay();
            media=player;viewport.Children.Add(player);status.Text="Opening video…";
            player.MediaOpened+=(s,e)=>{
                if(disposed || !ReferenceEquals(media,player))return;
                Safe(()=>{
                    bool startNow=playRequested;
                    preparing=false;opened=true;playRequested=false;
                    duration=player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
                    if(Double.IsNaN(duration) || Double.IsInfinity(duration) || duration<0)duration=0;
                    seek.Maximum=Math.Max(1,duration);SetEnabled(true);paused=!startNow;
                    if(speedRatio!=1.0)player.SpeedRatio=speedRatio;
                    if(startNow)player.Play();else player.Pause();
                    play.Content=paused ? "Play" : "Pause";
                    status.Text=paused ? "Ready · click screen to play" : (speedRatio==1.0 ? "Playing" : "Playing ("+speed.Content+")");
                    UpdateCenterBadge();
                    timer.Start();UpdateTime(0);
                });
            };
            player.MediaFailed+=(s,e)=>{if(ReferenceEquals(media,player))Fail("Windows could not play this file. The saved poster is still available. "+(e.ErrorException==null ? "" : e.ErrorException.GetBaseException().Message));};
            player.MediaEnded+=(s,e)=>{
                if(!ReferenceEquals(media,player))return;
                if(Loop && !paused){Safe(()=>{player.Position=TimeSpan.Zero;player.Play();});return;}
                ended=true;paused=true;play.Content="Replay";status.Text="Playback ended · click screen to replay";UpdateTime(duration);UpdateCenterBadge();ShowOverlay();
            };
            // Pause prerolls the Windows media pipeline without starting audio or advancing the clip.
            player.Source=new Uri(path,UriKind.Absolute);player.Pause();
        }
        void SetEnabled(bool ready) {play.IsEnabled=!disposed;stop.IsEnabled=ready;back.IsEnabled=forward.IsEnabled=seek.IsEnabled=ready && duration>0;}
        void Tick(object sender,EventArgs args) {
            if(preparing && media==null && preparationStage!=null){
                string stage=preparationStage();
                if(stage!=null)status.Text=stage;
            }
            if(opened && !dragging && !ended){
                Safe(()=>{
                    double seconds=media.Position.TotalSeconds;
                    // MediaOpened can precede the first displayed frame. Keep the poster through paused preroll.
                    if(!paused && media.Opacity==0 && media.NaturalVideoWidth>0 && !media.IsBuffering && seconds>0){
                        media.Opacity=1;
                        UpdateCenterBadge();
                    }
                    updating=true;try{seek.Value=Math.Min(duration,seconds);}finally{updating=false;}
                    UpdateTime(seconds);
                });
            }
        }
        void SeekTo(double seconds) {
            if(!opened || duration<=0 || updating)return;
            Safe(()=>{seconds=Math.Max(0,Math.Min(duration,seconds));media.Position=TimeSpan.FromSeconds(seconds);ended=false;updating=true;try{seek.Value=seconds;}finally{updating=false;}UpdateTime(seconds);UpdateCenterBadge();});
        }
        static string Clock(double seconds) {var span=TimeSpan.FromSeconds(Math.Max(0,seconds));return span.TotalHours>=1 ? ((int)span.TotalHours)+":"+span.Minutes.ToString("00")+":"+span.Seconds.ToString("00") : ((int)span.TotalMinutes)+":"+span.Seconds.ToString("00");}
        void UpdateTime(double seconds) {time.Text=Clock(seconds)+" / "+(duration>0 ? Clock(duration) : "--:--");}
        void UpdateMute() {if(media!=null)muted=media.IsMuted;mute.Content=muted ? "Unmute" : "Mute";AutomationProperties.SetName(mute,muted ? "Unmute video" : "Mute video");}
        void Fail(string message) {userStopped=true;Reset();status.Text=message;UpdateCenterBadge();ShowOverlay();}
        void Reset() {
            ++generation;timer.Stop();hideTimer.Stop();opened=paused=ended=dragging=preparing=playRequested=false;duration=0;
            var cancel=cancelResolution;cancelResolution=null;
            if(cancel!=null)cancel();
            var old=media;media=null;
            if(old!=null){muted=old.IsMuted;try{old.Close();old.Source=null;}finally{viewport.Children.Remove(old);}}
            if(TemporarySource)PlaybackProxy.Release(playbackPath);
            playbackPath=null;
            updating=true;try{seek.Value=0;}finally{updating=false;}
            time.Text="0:00 / --:--";play.Content="Play";SetEnabled(false);UpdateMute();UpdateCenterBadge();
        }
        public void Dispose() {
            if(disposed)return;
            disposed=true;userStopped=false;
            if(poster!=null)PosterSource.RemoveValueChanged(poster,PosterChanged);
            Reset();DetachHoverHost();timer.Tick-=Tick;
        }
    }
}
