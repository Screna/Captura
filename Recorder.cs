// Adopted from SharpAvi Screencast Sample by Vasilli Masillov
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ManagedWin32.Api;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpAvi.Output;

namespace Captura
{
    class Recorder : IDisposable
    {
        #region Fields
        AviWriter writer;
        RecorderParams Params;
        IAviVideoStream videoStream;
        IAviAudioStream audioStream;
        IWaveIn audioSource;
        Thread screenThread;
        ManualResetEvent stopThread = new ManualResetEvent(false);
        AutoResetEvent videoFrameWritten = new AutoResetEvent(false),
            audioBlockWritten = new AutoResetEvent(false);
        public bool IsPaused = false;
        WaveFileWriter WaveWriter;
        IWavePlayer SilencePlayer;
        #endregion

        public Recorder(RecorderParams Params)
        {
            this.Params = Params;

            if (Params.CaptureVideo)
            {
                // Create AVI writer and specify FPS
                writer = Params.CreateAviWriter();

                // Create video stream
                videoStream = Params.CreateVideoStream(writer);
                // Set only name. Other properties were when creating stream, 
                // either explicitly by arguments or implicitly by the encoder used
                videoStream.Name = "Captura";
            }

            try
            {
                int AudioSourceId = int.Parse(Params.AudioSourceId);

                if (AudioSourceId != -1)
                {
                    if (Params.CaptureVideo)
                    {
                        audioStream = Params.CreateAudioStream(writer);
                        audioStream.Name = "Voice";
                    }

                    audioSource = new WaveInEvent
                    {
                        DeviceNumber = AudioSourceId,
                        WaveFormat = Params.WaveFormat,
                        // Buffer size to store duration of 1 frame
                        BufferMilliseconds = (int)Math.Ceiling(1000 / writer.FramesPerSecond),
                        NumberOfBuffers = 3,
                    };
                }
            }
            catch
            {
                var dev = Params.LoopbackDevice;

                SilencePlayer = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);

                SilencePlayer.Init(new SilenceProvider(Params.WaveFormat));

                SilencePlayer.Play();

                if (Params.CaptureVideo)
                {
                    audioStream = Params.CreateAudioStream(writer);
                    audioStream.Name = "Loopback";
                }

                audioSource = new WasapiLoopbackCapture(dev) { ShareMode = AudioClientShareMode.Shared };
            }

            if (Params.CaptureVideo)
            {
                screenThread = new Thread(RecordScreen)
                {
                    Name = typeof(Recorder).Name + ".RecordScreen",
                    IsBackground = true
                };
            }
            else WaveWriter = Params.CreateWaveWriter();

            if (Params.CaptureVideo) screenThread.Start();

            if (audioSource != null)
            {
                audioSource.DataAvailable += AudioDataAvailable;

                if (Params.CaptureVideo)
                {
                    videoFrameWritten.Set();
                    audioBlockWritten.Reset();
                }

                audioSource.StartRecording();
            }
        }

        public void Dispose()
        {
            if (IsPaused) Resume();

            if (SilencePlayer != null)
            {
                SilencePlayer.Stop();
                SilencePlayer.Dispose();
                SilencePlayer = null;
            }

            if (Params.CaptureVideo)
            {
                if (!stopThread.SafeWaitHandle.IsClosed
                    && !stopThread.SafeWaitHandle.IsInvalid)
                    stopThread.Set();
                screenThread.Abort();
            }

            if (audioSource != null)
            {
                audioSource.StopRecording();
                audioSource.DataAvailable -= AudioDataAvailable;
            }

            if (Params.CaptureVideo)
            {
                // Close writer: the remaining data is written to a file and file is closed
                writer.Close();

                if (!stopThread.SafeWaitHandle.IsClosed) stopThread.Close();
            }
            else WaveWriter.Dispose();
        }

        public void Pause()
        {
            if (!IsPaused)
            {
                if (SilencePlayer != null) SilencePlayer.Pause();

                if (Params.CaptureVideo) screenThread.Suspend();

                if (audioSource != null) audioSource.StopRecording();

                IsPaused = true;
            }
        }

        public void Resume()
        {
            if (IsPaused)
            {
                if (SilencePlayer != null) SilencePlayer.Play();

                if (Params.CaptureVideo) screenThread.Resume();

                if (audioSource != null)
                {
                    if (Params.CaptureVideo)
                    {
                        videoFrameWritten.Set();
                        audioBlockWritten.Reset();
                    }
                    audioSource.StartRecording();
                }

                IsPaused = false;
            }
        }

        void RecordScreen()
        {
            try
            {
                var frameInterval = TimeSpan.FromSeconds(1 / (double)writer.FramesPerSecond);
                var buffer = new byte[App.DesktopWidth * App.DesktopHeight * 4];
                Task videoWriteTask = null;
                var isFirstFrame = true;
                var timeTillNextFrame = TimeSpan.Zero;

                while (!stopThread.WaitOne(timeTillNextFrame))
                {
                    var timestamp = DateTime.Now;

                    ScreenShot(buffer);

                    // Wait for the previous frame is written
                    if (!isFirstFrame)
                    {
                        videoWriteTask.Wait();
                        videoFrameWritten.Set();
                    }

                    if (audioStream != null && !Params.IsLoopback)
                        if (WaitHandle.WaitAny(new WaitHandle[] { audioBlockWritten, stopThread }) == 1)
                            break;

                    // Start asynchronous (encoding and) writing of the new frame
                    videoWriteTask = videoStream.WriteFrameAsync(true, buffer, 0, buffer.Length);

                    timeTillNextFrame = timestamp + frameInterval - DateTime.Now;
                    if (timeTillNextFrame < TimeSpan.Zero) timeTillNextFrame = TimeSpan.Zero;

                    isFirstFrame = false;
                }

                // Wait for the last frame is written
                if (!isFirstFrame) videoWriteTask.Wait();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public static Bitmap ScreenShot(IntPtr hWnd, bool IncludeCursor, bool ScreenCasting = true)
        {
            int CursorX = 0, CursorY = 0;
            Rectangle Rect = default(Rectangle);

            if (hWnd != App.Desktop)
            {
                var rect = new RECT();
                User32.GetWindowRect(hWnd, ref rect);

                Rect = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

                if (!ScreenCasting) User32.SetWindowPos(hWnd, (IntPtr)(-1), 0, 0, 0, 0, SetWindowPositionFlags.NoMove | SetWindowPositionFlags.NoSize);
            }
            else Rect = App.DesktopRect;

            var BMP = new Bitmap(App.DesktopWidth, App.DesktopHeight);
            using (var g = Graphics.FromImage(BMP))
            {
                if (ScreenCasting) g.FillRectangle(Brushes.White, App.DesktopRect);

                g.CopyFromScreen(Rect.Location, Rect.Location, Rect.Size, CopyPixelOperation.SourceCopy);

                #region Include Cursor
                if (IncludeCursor)
                {
                    IntPtr hIcon = IntPtr.Zero;
                    CursorInfo ci = new CursorInfo() { cbSize = Marshal.SizeOf(typeof(CursorInfo)) };

                    IconInfo icInfo;

                    if (User32.GetCursorInfo(out ci))
                    {
                        if (ci.flags == User32.CURSOR_SHOWING)
                        {
                            hIcon = User32.CopyIcon(ci.hCursor);
                            if (User32.GetIconInfo(hIcon, out icInfo))
                            {
                                CursorX = ci.ptScreenPos.X - ((int)icInfo.xHotspot);
                                CursorY = ci.ptScreenPos.Y - ((int)icInfo.yHotspot);
                            }
                        }
                    }

                    if (hIcon != IntPtr.Zero)
                    {
                        Bitmap CursorBMP = Icon.FromHandle(hIcon).ToBitmap();
                        g.DrawImage(CursorBMP, CursorX, CursorY, CursorBMP.Width, CursorBMP.Height);
                    }
                }
                #endregion

                g.Flush();

            }

            if (!ScreenCasting) User32.SetWindowPos(hWnd, (IntPtr)(-2), 0, 0, 0, 0, SetWindowPositionFlags.NoMove | SetWindowPositionFlags.NoSize);

            return BMP;
        }

        public void ScreenShot(byte[] Buffer)
        {
            using (var BMP = ScreenShot(Params.hWnd, Params.IncludeCursor))
            {
                var bits = BMP.LockBits(App.DesktopRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(bits.Scan0, Buffer, 0, Buffer.Length);
                BMP.UnlockBits(bits);
            }
        }

        void AudioDataAvailable(object sender, WaveInEventArgs e)
        {
            if (Params.CaptureVideo)
            {
                if (Params.IsLoopback) audioStream.WriteBlock(e.Buffer, 0, e.BytesRecorded);
                else
                {
                    var signalled = WaitHandle.WaitAny(new WaitHandle[] { videoFrameWritten, stopThread });
                    if (signalled == 0)
                    {
                        audioStream.WriteBlock(e.Buffer, 0, e.BytesRecorded);

                        audioBlockWritten.Set();
                    }
                }
            }
            else WaveWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }
}
