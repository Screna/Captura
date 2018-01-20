﻿using Captura.Models;
using Screna;
using Screna.Audio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Win32;
using Timer = System.Timers.Timer;
using Window = Screna.Window;

namespace Captura.ViewModels
{
    public partial class MainViewModel : ViewModelBase, IDisposable
    {
        #region Fields
        Timer _timer;
        readonly Timing _timing = new Timing();
        IRecorder _recorder;
        string _currentFileName;
        bool isVideo;
        public static readonly RectangleConverter RectangleConverter = new RectangleConverter();
        readonly SynchronizationContext _syncContext = SynchronizationContext.Current;

        IWebCamProvider _cam;
        public IWebCamProvider WebCamProvider
        {
            get => _cam;
            set
            {
                _cam = value;

                OnPropertyChanged();
            }
        }

        readonly ISystemTray _systemTray;
        readonly IRegionProvider _regionProvider;
        readonly WebcamOverlay _webcamOverlay;
        #endregion
        
        public MainViewModel(AudioViewModel AudioViewModel, VideoViewModel VideoViewModel, ISystemTray SystemTray, IRegionProvider RegionProvider, IWebCamProvider WebCamProvider, WebcamOverlay WebcamOverlay)
        {
            this.AudioViewModel = AudioViewModel;
            this.VideoViewModel = VideoViewModel;
            _systemTray = SystemTray;
            _regionProvider = RegionProvider;
            this.WebCamProvider = WebCamProvider;
            _webcamOverlay = WebcamOverlay;

            #region Commands
            ScreenShotCommand = new DelegateCommand(() => CaptureScreenShot());
            
            ScreenShotActiveCommand = new DelegateCommand(() => SaveScreenShot(ScreenShotWindow(Window.ForegroundWindow)));

            ScreenShotDesktopCommand = new DelegateCommand(() => SaveScreenShot(ScreenShotWindow(Window.DesktopWindow)));

            RecordCommand = new DelegateCommand(async () =>
            {
                if (RecorderState == RecorderState.NotRecording)
                    StartRecording();
                else await StopRecording();
            });

            RefreshCommand = new DelegateCommand(() =>
            {
                WindowProvider.RefreshDesktopSize();

                VideoViewModel.RefreshVideoSources();

                VideoViewModel.RefreshCodecs();

                AudioViewModel.AudioSource.Refresh();

                WebCamProvider.Refresh();

                Status.LocalizationKey = nameof(LanguageManager.Refreshed);
            });
            
            PauseCommand = new DelegateCommand(OnPauseExecute, false);
            #endregion

            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        }

        void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend && RecorderState == RecorderState.Recording)
            {
                OnPauseExecute();
            }
        }

        void OnPauseExecute()
        {
            if (RecorderState == RecorderState.Paused)
            {
                _systemTray.HideNotification();

                _recorder.Start();
                _timing?.Start();
                _timer?.Start();

                RecorderState = RecorderState.Recording;
                Status.LocalizationKey = nameof(LanguageManager.Recording);
            }
            else
            {
                _recorder.Stop();
                _timer?.Stop();
                _timing?.Pause();

                RecorderState = RecorderState.Paused;
                Status.LocalizationKey = nameof(LanguageManager.Paused);

                _systemTray.ShowTextNotification(LanguageManager.Paused, 3000, null);
            }
        }

        void RestoreRemembered()
        {
            #region Restore Video Source
            void VideoSource()
            {
                VideoViewModel.SelectedVideoSourceKind = Settings.Instance.LastSourceKind;

                var source = VideoViewModel.AvailableVideoSources.FirstOrDefault(window => window.ToString() == Settings.Instance.LastSourceName);

                if (source != null)
                    VideoViewModel.SelectedVideoSource = source;
            }

            switch (Settings.Instance.LastSourceKind)
            {
                case VideoSourceKind.Window:
                case VideoSourceKind.NoVideo:
                case VideoSourceKind.Screen:
                    VideoSource();
                    break;

                case VideoSourceKind.Region:
                    VideoViewModel.SelectedVideoSourceKind = VideoSourceKind.Region;

                    if (RectangleConverter.ConvertFromInvariantString(Settings.Instance.LastSourceName) is Rectangle rect)
                        _regionProvider.SelectedRegion = rect;
                    break;
            }
            #endregion

            // Restore Video Codec
            if (VideoViewModel.AvailableVideoWriterKinds.Contains(Settings.Instance.LastVideoWriterKind))
            {
                VideoViewModel.SelectedVideoWriterKind = Settings.Instance.LastVideoWriterKind;

                var codec = VideoViewModel.AvailableVideoWriters.FirstOrDefault(c => c.ToString() == Settings.Instance.LastVideoWriterName);

                if (codec != null)
                    VideoViewModel.SelectedVideoWriter = codec;
            }
            
            // Restore Microphone
            if (!string.IsNullOrEmpty(Settings.Instance.LastMicName))
            {
                var source = AudioViewModel.AudioSource.AvailableRecordingSources.FirstOrDefault(codec => codec.ToString() == Settings.Instance.LastMicName);

                if (source != null)
                    AudioViewModel.AudioSource.SelectedRecordingSource = source;
            }

            // Restore Loopback Speaker
            if (!string.IsNullOrEmpty(Settings.Instance.LastSpeakerName))
            {
                var source = AudioViewModel.AudioSource.AvailableLoopbackSources.FirstOrDefault(codec => codec.ToString() == Settings.Instance.LastSpeakerName);

                if (source != null)
                    AudioViewModel.AudioSource.SelectedLoopbackSource = source;
            }

            // Restore ScreenShot Format
            if (!string.IsNullOrEmpty(Settings.Instance.LastScreenShotFormat))
            {
                var format = ScreenShotImageFormats.FirstOrDefault(f => f.ToString() == Settings.Instance.LastScreenShotFormat);

                if (format != null)
                    SelectedScreenShotImageFormat = format;
            }

            // Restore ScreenShot Target
            if (!string.IsNullOrEmpty(Settings.Instance.LastScreenShotSaveTo))
            {
                var saveTo = VideoViewModel.AvailableImageWriters.FirstOrDefault(s => s.ToString() == Settings.Instance.LastScreenShotSaveTo);

                if (saveTo != null)
                    VideoViewModel.SelectedImageWriter = saveTo.Source;
            }
        }

        bool _persist, _hotkeys;

        public void Init(bool Persist, bool Timer, bool Remembered, bool Hotkeys)
        {
            _persist = Persist;
            _hotkeys = Hotkeys;

            if (Timer)
            {
                _timer = new Timer(500);
                _timer.Elapsed += TimerOnElapsed;
            }

            AudioViewModel.AudioSource.PropertyChanged += (Sender, Args) =>
            {
                switch (Args.PropertyName)
                {
                    case nameof(AudioSource.SelectedRecordingSource):
                    case nameof(AudioSource.SelectedLoopbackSource):
                    case null:
                    case "":
                        CheckFunctionalityAvailability();
                        break;
                }
            };

            VideoViewModel.PropertyChanged += (Sender, Args) =>
            {
                switch (Args.PropertyName)
                {
                    case nameof(VideoViewModel.SelectedVideoSourceKind):
                    case nameof(VideoViewModel.SelectedVideoSource):
                    case null:
                    case "":
                        CheckFunctionalityAvailability();
                        break;
                }
            };
            
            // If Output Dircetory is not set. Set it to Documents\Captura\
            if (string.IsNullOrWhiteSpace(Settings.Instance.OutPath))
                Settings.Instance.OutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Captura\\");

            // Create the Output Directory if it does not exist
            if (!Directory.Exists(Settings.Instance.OutPath))
                Directory.CreateDirectory(Settings.Instance.OutPath);

            // Register ActionServices
            ServiceProvider.Register<Action>(ServiceName.Recording, () => RecordCommand.ExecuteIfCan());
            ServiceProvider.Register<Action>(ServiceName.Pause, () => PauseCommand.ExecuteIfCan());
            ServiceProvider.Register<Action>(ServiceName.ScreenShot, () => ScreenShotCommand.ExecuteIfCan());
            ServiceProvider.Register<Action>(ServiceName.ActiveScreenShot, () => SaveScreenShot(ScreenShotWindow(Window.ForegroundWindow)));
            ServiceProvider.Register<Action>(ServiceName.DesktopScreenShot, () => SaveScreenShot(ScreenShotWindow(Window.DesktopWindow)));

            // Register Hotkeys if not console
            if (_hotkeys)
                HotKeyManager.RegisterAll();

            VideoViewModel.Init();

            if (Remembered)
                RestoreRemembered();
        }

        void Remember()
        {
            #region Remember Video Source
            switch (VideoViewModel.SelectedVideoSourceKind)
            {
                case VideoSourceKind.Window:
                case VideoSourceKind.Screen:
                case VideoSourceKind.NoVideo:
                    Settings.Instance.LastSourceKind = VideoViewModel.SelectedVideoSourceKind;
                    Settings.Instance.LastSourceName = VideoViewModel.SelectedVideoSource.ToString();
                    break;

                case VideoSourceKind.Region:
                    Settings.Instance.LastSourceKind = VideoSourceKind.Region;
                    var rect = _regionProvider.SelectedRegion;
                    Settings.Instance.LastSourceName = RectangleConverter.ConvertToInvariantString(rect);
                    break;

                default:
                    Settings.Instance.LastSourceKind = VideoSourceKind.Screen;
                    Settings.Instance.LastSourceName = "";
                    break;
            }
            #endregion

            // Remember Video Codec
            Settings.Instance.LastVideoWriterKind = VideoViewModel.SelectedVideoWriterKind;
            Settings.Instance.LastVideoWriterName = VideoViewModel.SelectedVideoWriter.ToString();

            // Remember Audio Sources
            Settings.Instance.LastMicName = AudioViewModel.AudioSource.SelectedRecordingSource.ToString();
            Settings.Instance.LastSpeakerName = AudioViewModel.AudioSource.SelectedLoopbackSource.ToString();
            
            // Remember ScreenShot Format
            Settings.Instance.LastScreenShotFormat = SelectedScreenShotImageFormat.ToString();

            // Remember ScreenShot Target
            Settings.Instance.LastScreenShotSaveTo = VideoViewModel.SelectedImageWriter.ToString();
        }

        // Call before Exit to free LanguageManager
        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

            if (_hotkeys)
                HotKeyManager.Dispose();

            AudioViewModel.Dispose();

            RecentViewModel.Dispose();
            
            CustomOverlaysViewModel.Instance.Dispose();

            // Remember things if not console.
            if (_persist)
            {
                Remember();

                Settings.Instance.Save();
            }
        }
        
        async void TimerOnElapsed(object Sender, ElapsedEventArgs Args)
        {
            TimeSpan = TimeSpan.FromSeconds((int)_timing.Elapsed.TotalSeconds);
            
            // If Capture Duration is set and reached
            if (Duration > 0 && TimeSpan.TotalSeconds >= Duration)
            {
                if (_syncContext != null)
                    _syncContext.Post(async State => await StopRecording(), null);
                else await StopRecording();
            }
        }
        
        void CheckFunctionalityAvailability()
        {
            var audioAvailable = AudioViewModel.AudioSource.AudioAvailable;

            var videoAvailable = VideoViewModel.SelectedVideoSourceKind != VideoSourceKind.NoVideo;
            
            RecordCommand.RaiseCanExecuteChanged(audioAvailable || videoAvailable);

            ScreenShotCommand.RaiseCanExecuteChanged(videoAvailable);
        }

        public void SaveScreenShot(Bitmap bmp, string FileName = null)
        {
            // Save to Disk or Clipboard
            if (bmp != null)
            {
                VideoViewModel.SelectedImageWriter.Save(bmp, SelectedScreenShotImageFormat, FileName, Status, RecentViewModel);
            }
            else _systemTray.ShowTextNotification(LanguageManager.ImgEmpty, 5000, null);
        }

        public Bitmap ScreenShotWindow(Window hWnd)
        {
            _systemTray.HideNotification();

            if (hWnd == Window.DesktopWindow)
            {
                return ScreenShot.Capture(Settings.Instance.IncludeCursor).Transform();
            }

            var bmp = ScreenShot.CaptureTransparent(hWnd,
                Settings.Instance.IncludeCursor,
                Settings.Instance.DoResize,
                Settings.Instance.ResizeWidth,
                Settings.Instance.ResizeHeight);

            // Capture without Transparency
            if (bmp == null)
            {
                try
                {
                    return ScreenShot.Capture(hWnd, Settings.Instance.IncludeCursor)?.Transform();
                }
                catch
                {
                    return null;
                }
            }

            return bmp.Transform(true);
        }

        public async void CaptureScreenShot(string FileName = null)
        {
            _systemTray.HideNotification();

            Bitmap bmp = null;

            var selectedVideoSource = VideoViewModel.SelectedVideoSource;
            var includeCursor = Settings.Instance.IncludeCursor;

            switch (VideoViewModel.SelectedVideoSourceKind)
            {
                case VideoSourceKind.Window:
                    var hWnd = (selectedVideoSource as WindowItem)?.Window ?? Window.DesktopWindow;

                    bmp = ScreenShotWindow(hWnd);
                    break;

                case VideoSourceKind.DesktopDuplication:
                case VideoSourceKind.Screen:
                    if (selectedVideoSource is FullScreenItem)
                    {
                        var hide = ServiceProvider.MainWindow.IsVisible && Settings.Instance.HideOnFullScreenShot;

                        if (hide)
                        {
                            ServiceProvider.MainWindow.IsVisible = false;

                            // Ensure that the Window is hidden
                            await Task.Delay(300);
                        }

                        bmp = ScreenShot.Capture(includeCursor);

                        if (hide)
                            ServiceProvider.MainWindow.IsVisible = true;
                    }
                    else if (selectedVideoSource is ScreenItem screen)
                    {
                        bmp = screen.Capture(includeCursor);
                    }
                    
                    bmp = bmp?.Transform();
                    break;

                case VideoSourceKind.Region:
                    bmp = ScreenShot.Capture(_regionProvider.SelectedRegion, includeCursor);
                    bmp = bmp.Transform();
                    break;
            }

            SaveScreenShot(bmp, FileName);
        }
        
        public void StartRecording(string FileName = null)
        {
            if (VideoViewModel.SelectedVideoWriterKind == VideoWriterKind.FFMpeg ||
                VideoViewModel.SelectedVideoWriterKind == VideoWriterKind.Streaming_Alpha ||
                (VideoViewModel.SelectedVideoSourceKind == VideoSourceKind.NoVideo && VideoViewModel.SelectedVideoSource is FFMpegAudioItem))
            {
                if (!FFMpegService.FFMpegExists)
                {
                    ServiceProvider.MessageProvider.ShowFFMpegUnavailable();

                    return;
                }

                FFMpegLog.Reset();
            }

            if (VideoViewModel.SelectedVideoWriterKind == VideoWriterKind.Gif
                && Settings.Instance.GifVariable
                && VideoViewModel.SelectedVideoSourceKind == VideoSourceKind.DesktopDuplication)
            {
                ServiceProvider.MessageProvider.ShowError("Using Variable Frame Rate GIF with Desktop Duplication is not supported.");

                return;
            }

            if (VideoViewModel.SelectedVideoWriterKind == VideoWriterKind.Gif)
            {
                if (AudioViewModel.AudioSource.SelectedLoopbackSource != NoSoundItem.Instance
                    || AudioViewModel.AudioSource.SelectedRecordingSource != NoSoundItem.Instance)
                {
                    if (!ServiceProvider.MessageProvider.ShowYesNo("Audio won't be included in the recording.\nDo you want to record?", "Gif does not support Audio"))
                    {
                        return;
                    }
                }
            }

            if (Duration != 0 && (StartDelay > Duration * 1000))
            {
                ServiceProvider.MessageProvider.ShowError(LanguageManager.DelayGtDuration);

                return;
            }

            IImageProvider imgProvider;

            try
            {
                imgProvider = GetImageProvider();
            }
            catch (NotSupportedException e) when (VideoViewModel.SelectedVideoSourceKind == VideoSourceKind.DesktopDuplication)
            {
                var yes = ServiceProvider.MessageProvider.ShowYesNo($"{e.Message}\n\nDo you want to turn off Desktop Duplication.", LanguageManager.ErrorOccured);

                if (yes)
                    VideoViewModel.SelectedVideoSourceKind = VideoSourceKind.Screen;

                return;
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowError(e.ToString());

                return;
            }

            _regionProvider.Lock();

            _systemTray.HideNotification();

            if (Settings.Instance.MinimizeOnStart)
                ServiceProvider.MainWindow.IsMinimized = true;
            
            Settings.Instance.EnsureOutPath();

            RecorderState = RecorderState.Recording;
            
            isVideo = VideoViewModel.SelectedVideoSourceKind != VideoSourceKind.NoVideo;
            
            var extension = VideoViewModel.SelectedVideoWriter.Extension;

            if (VideoViewModel.SelectedVideoSource is NoVideoItem x)
                extension = x.Extension;

            _currentFileName = FileName ?? Path.Combine(Settings.Instance.OutPath, $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}{extension}");

            Status.LocalizationKey = StartDelay > 0 ? nameof(LanguageManager.Waiting) : nameof(LanguageManager.Recording);

            _timer?.Stop();
            TimeSpan = TimeSpan.Zero;
            
            var audioSource = AudioViewModel.AudioSource.GetAudioSource();
            
            var videoEncoder = GetVideoFileWriter(imgProvider, audioSource);
            
            if (_recorder == null)
            {
                if (isVideo)
                    _recorder = new Recorder(videoEncoder, imgProvider, Settings.Instance.FrameRate, audioSource);

                else if (VideoViewModel.SelectedVideoSource is NoVideoItem audioWriter)
                    _recorder = new Recorder(audioWriter.GetAudioFileWriter(_currentFileName, audioSource.WaveFormat, Settings.Instance.AudioQuality), audioSource);
            }
            
            _recorder.ErrorOccured += E =>
            {
                if (_syncContext != null)
                    _syncContext.Post(d => OnErrorOccured(E), null);
                else OnErrorOccured(E);
            };
            
            if (StartDelay > 0)
            {
                Task.Factory.StartNew(async () =>
                {
                    await Task.Delay(StartDelay);

                    _recorder.Start();
                });
            }
            else _recorder.Start();

            _timing?.Start();
            _timer?.Start();
        }

        void OnErrorOccured(Exception E)
        {
            Status.LocalizationKey = nameof(LanguageManager.ErrorOccured);
                        
            AfterRecording();

            ServiceProvider.MessageProvider.ShowError(E.ToString());
        }

        void AfterRecording()
        {
            RecorderState = RecorderState.NotRecording;

            _recorder = null;

            _timer?.Stop();
            _timing.Stop();
            
            if (Settings.Instance.MinimizeOnStart)
                ServiceProvider.MainWindow.IsMinimized = false;

            _regionProvider.Release();
        }

        IVideoFileWriter GetVideoFileWriter(IImageProvider ImgProvider, IAudioProvider AudioProvider)
        {
            if (VideoViewModel.SelectedVideoSourceKind == VideoSourceKind.NoVideo)
                return null;
            
            IVideoFileWriter videoEncoder = null;
            
            var encoder = VideoViewModel.SelectedVideoWriter.GetVideoFileWriter(_currentFileName, Settings.Instance.FrameRate, Settings.Instance.VideoQuality, ImgProvider, Settings.Instance.AudioQuality, AudioProvider);

            switch (encoder)
            {
                case GifWriter gif:
                    if (Settings.Instance.GifVariable)
                        _recorder = new VFRGifRecorder(gif, ImgProvider);
                    
                    else videoEncoder = gif;
                    break;

                default:
                    videoEncoder = encoder;
                    break;
            }

            return videoEncoder;
        }
        
        IImageProvider GetImageProvider()
        {
            Func<Point, Point> transform = p => p;

            var imageProvider = VideoViewModel.SelectedVideoSource?.GetImageProvider(Settings.Instance.IncludeCursor, out transform);

            if (imageProvider == null)
                return null;

            var overlays = new List<IOverlay> { _webcamOverlay };
                        
            // Mouse Click overlay should be drawn below cursor.
            if (MouseKeyHookAvailable && (Settings.Instance.Clicks.Display || Settings.Instance.Keystrokes.Display))
                overlays.Add(new MouseKeyHook(Settings.Instance.Clicks, Settings.Instance.Keystrokes));
            
            // Custom Overlays
            overlays.AddRange(CustomOverlaysViewModel.Instance.Collection.Where(M => M.Display).Select(M => new CustomOverlay(M, () => TimeSpan)));

            return new OverlayedImageProvider(imageProvider, transform, overlays.ToArray());
        }
        
        public async Task StopRecording()
        {
            Status.LocalizationKey = nameof(LanguageManager.Stopped);

            var savingRecentItem = RecentViewModel.Add(_currentFileName, isVideo ? RecentItemType.Video : RecentItemType.Audio, true);
            
            // Reference Recorder as it will be set to null
            var rec = _recorder;
            
            var task = Task.Run(() => rec.Dispose());
            
            AfterRecording();

            try
            {
                // Ensure saved
                await task;
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowError($"Error occured when stopping recording.\nThis might sometimes occur if you stop recording just as soon as you start it.\n\n{e}");

                return;
            }

            // After Save
            savingRecentItem.Saved();

            if (Settings.Instance.CopyOutPathToClipboard)
                savingRecentItem.FilePath.WriteToClipboard();
            
            _systemTray.ShowTextNotification((isVideo ? LanguageManager.VideoSaved : LanguageManager.AudioSaved) + ": " + Path.GetFileName(savingRecentItem.FilePath), 5000, () =>
            {
                ServiceProvider.LaunchFile(new ProcessStartInfo(savingRecentItem.FilePath));
            });
        }
    }
}