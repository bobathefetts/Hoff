param([Parameter(Mandatory)][string]$Assembly,[Parameter(Mandatory)][string]$Fixture,[Parameter(Mandatory)][string]$Snapshot,[switch]$PrepareBeforePlay)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$loaded=[Reflection.Assembly]::LoadFrom($Assembly)
$type=$loaded.GetType('MediaReviewer.VideoPlaybackControls',$true)
$viewport=New-Object Windows.Controls.Grid
$poster=New-Object Windows.Controls.Image
$viewport.Children.Add($poster) | Out-Null
Add-Type 'using System; public static class PlaybackFixture { public static Func<Action<string,string>,Action> Resolver(string path) { return done => { done(path, null); return null; }; } }'
$resolver=[PlaybackFixture]::Resolver($Fixture)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
# The constructor's third argument (an optional preparation-stage callback) has no default when called by reflection.
$player=$type.GetConstructors($flags)[0].Invoke([object[]]@($viewport.PSObject.BaseObject,$resolver,$null))
$dock=New-Object Windows.Controls.DockPanel
[Windows.Controls.DockPanel]::SetDock($player,'Bottom'); $dock.Children.Add($player) | Out-Null; $dock.Children.Add($viewport) | Out-Null
$window=New-Object Windows.Window -Property @{Title='Generated video playback check';Width=820;Height=540;Content=$dock;WindowStartupLocation='CenterScreen'}
function Pump([int]$Milliseconds) {
  $frame=New-Object Windows.Threading.DispatcherFrame
  $clock=New-Object Windows.Threading.DispatcherTimer
  $clock.Interval=[TimeSpan]::FromMilliseconds($Milliseconds)
  $clock.Tag=$frame
  $clock.add_Tick({param($sender,$args) $sender.Stop(); $sender.Tag.Continue=$false})
  $clock.Start(); [Windows.Threading.Dispatcher]::PushFrame($frame)
}
function Check([bool]$Condition,[string]$Message) {if(!$Condition){throw $Message}}
function Click($Button) {$Button.RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Button]::ClickEvent)))}
try {
  $window.Show();Pump 200
  $panel=$player.Child;$status=$panel.Children[0];$timeline=$panel.Children[1];$commands=$panel.Children[2]
  $play=$commands.Children[0];$stop=$commands.Children[1];$mute=$commands.Children[4];$volume=$commands.Children[6];$seek=$timeline.Children[1]
  Check ($viewport.Children.Count -eq 1) 'Unexpected autoplay'
  if($PrepareBeforePlay) {
    $preparation=[Diagnostics.Stopwatch]::StartNew()
    $type.GetMethod('Prepare',$flags).Invoke($player,@()) | Out-Null
    while(!$seek.IsEnabled -and $preparation.ElapsedMilliseconds -lt 15000){Pump 100}
    Check $seek.IsEnabled ('Preparation failed: '+$status.Text)
    Pump 350
    Check ($viewport.Children[1].Position.TotalSeconds -lt 0.15 -and $play.Content -eq 'Play') 'Prepared video must remain paused until Play'
    'Prepared paused in '+$preparation.ElapsedMilliseconds+' ms (includes 350 ms no-autoplay check).'
  }
  $clickTime=[Diagnostics.Stopwatch]::StartNew()
  Click $play
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($play.Content -ne 'Pause' -and $watch.ElapsedMilliseconds -lt 15000){Pump 100}
  Check ($play.Content -eq 'Pause') ('Playback failed: '+$status.Text)
  'Play to ready: '+$clickTime.ElapsedMilliseconds+' ms; prepared='+$PrepareBeforePlay
  Pump 800
  $media=$viewport.Children[1]
  Check ($media.NaturalVideoWidth -eq 320 -and $media.NaturalVideoHeight -eq 180) 'Decoded video dimensions differ'
  Check ($media.Position.TotalSeconds -gt 0) 'Playback did not advance'
  Click $play;Pump 100
  Check ($play.Content -eq 'Play') 'Pause button failed'
  $paused=$media.Position.TotalSeconds;Pump 350
  Check ([Math]::Abs($paused-$media.Position.TotalSeconds) -lt 0.15) 'Video advanced while paused'
  $volume.Value=0.2;Click $mute
  Check ($media.IsMuted -and [Math]::Abs($media.Volume-0.2) -lt 0.01) 'Volume or mute did not reach player'
  $seek.Value=4;Pump 200
  Check ([Math]::Abs($media.Position.TotalSeconds-4) -lt 0.5) 'Seek failed'
  Click $play;Pump 450
  Check ($media.Position.TotalSeconds -gt 4) 'Resume after seeking failed'
  $window.UpdateLayout()
  $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$window.ActualWidth),([int]$window.ActualHeight),96,96,([Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($window);$png=New-Object Windows.Media.Imaging.PngBitmapEncoder;$png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $out=[IO.File]::Create($Snapshot);try{$png.Save($out)}finally{$out.Dispose()}
  Click $stop
  Check ($viewport.Children.Count -eq 1 -and !$seek.IsEnabled) 'Stop did not restore poster and disable seeking'
  $stream=[IO.File]::Open($Fixture,'Open','Read','None');$stream.Dispose()
  'PASS: generated video open, decode, play, pause, seek, resume, volume, mute, stop, source release.'
} finally {$player.Dispose();$window.Close()}
