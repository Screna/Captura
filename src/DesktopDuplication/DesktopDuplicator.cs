﻿// Adapted from https://github.com/jasonpang/desktop-duplication-net

using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using Device = SharpDX.Direct3D11.Device;
using DRectangle = System.Drawing.Rectangle;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace DesktopDuplication
{
    public class DesktopDuplicator : IDisposable
    {
        #region Fields
        readonly Device _device;
        readonly Texture2DDescription _textureDesc;
        OutputDescription _outputDesc;
        readonly OutputDuplication _deskDupl;

        Texture2D _desktopImageTexture;
        OutputDuplicateFrameInformation _frameInfo;

        DRectangle _rect;
        #endregion

        public DesktopDuplicator(DRectangle Rect, int Monitor, int Adapter = 0)
        {
            _rect = Rect;

            Adapter1 adapter;
            try
            {
                adapter = new Factory1().GetAdapter1(Adapter);
            }
            catch (SharpDXException e)
            {
                throw new Exception("Could not find the specified graphics card adapter.", e);
            }

            _device = new Device(adapter);

            Output output;
            try
            {
                output = adapter.GetOutput(Monitor);
            }
            catch (SharpDXException e)
            {
                throw new Exception("Could not find the specified output device.", e);
            }

            var output1 = output.QueryInterface<Output1>();
            _outputDesc = output.Description;
            
            _textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = _rect.Width,
                Height = _rect.Height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging
            };

            try
            {
                _deskDupl = output1.DuplicateOutput(_device);
            }
            catch (SharpDXException e)
            {
                if (e.ResultCode.Code == SharpDX.DXGI.ResultCode.NotCurrentlyAvailable.Result.Code)
                {
                    throw new Exception("There is already the maximum number of applications using the Desktop Duplication API running, please close one of the applications and try again.", e);
                }
            }
        }

        public void UpdateRectLocation(System.Drawing.Point P)
        {
            _rect.Location = P;
        }

        Bitmap lastFrame;

        public Bitmap GetLatestFrame()
        {
            // Try to get the latest frame; this may timeout
            if (!RetrieveFrame())
                return lastFrame ?? new Bitmap(_rect.Width, _rect.Height);

            try
            {
                return ProcessFrame();
            }
            finally
            {
                ReleaseFrame();
            }
        }

        /// <summary>
        /// Returns true on success, false on timeout
        /// </summary>
        bool RetrieveFrame()
        {
            if (_desktopImageTexture == null)
                _desktopImageTexture = new Texture2D(_device, _textureDesc);
                        
            try
            {
                _deskDupl.AcquireNextFrame(0, out _frameInfo, out var desktopResource);

                using (var tempTexture = desktopResource.QueryInterface<Texture2D>())
                    _device.ImmediateContext.CopySubresourceRegion(tempTexture, 0, new ResourceRegion(_rect.Left, _rect.Top, 0, _rect.Right, _rect.Bottom, 1), _desktopImageTexture, 0);

                desktopResource.Dispose();

                return true;
            }
            catch (SharpDXException e)
            {
                if (e.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    return false;
                }

                if (e.ResultCode.Failure)
                {
                    throw new Exception("Failed to acquire next frame.", e);
                }

                throw;
            }
        }
        
        Bitmap ProcessFrame()
        {
            // Get the desktop capture texture
            var mapSource = _device.ImmediateContext.MapSubresource(_desktopImageTexture, 0, MapMode.Read, MapFlags.None);

            lastFrame = new Bitmap(_rect.Width, _rect.Height, PixelFormat.Format32bppRgb);

            // Copy pixels from screen capture Texture to GDI bitmap
            var mapDest = lastFrame.LockBits(new DRectangle(0, 0, _rect.Width, _rect.Height), ImageLockMode.WriteOnly, lastFrame.PixelFormat);

            Utilities.CopyMemory(mapDest.Scan0, mapSource.DataPointer, _rect.Width * _rect.Height * 4);
                        
            // Release source and dest locks
            lastFrame.UnlockBits(mapDest);
            _device.ImmediateContext.UnmapSubresource(_desktopImageTexture, 0);
            return lastFrame;
        }
        
        void ReleaseFrame()
        {
            try
            {
                _deskDupl.ReleaseFrame();
            }
            catch (SharpDXException e)
            {
                if (e.ResultCode.Failure)
                {
                    throw new Exception("Failed to release frame.", e);
                }
            }
        }

        public void Dispose()
        {
            _deskDupl?.Dispose();
            _desktopImageTexture?.Dispose();
            _device?.Dispose();
        }
    }
}
