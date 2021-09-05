﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

using SharpGen.Runtime;

using Vortice.DXGI;
using Vortice.DXGI.Debug;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

using FlyleafLib.MediaFramework.MediaFrame;
using VideoDecoder = FlyleafLib.MediaFramework.MediaDecoder.VideoDecoder;

namespace FlyleafLib.MediaFramework.MediaRenderer
{
    public unsafe class Renderer : IDisposable
    {
        public Config           Config          { get; private set; }
        public int              UniqueId        { get; private set; }

        public Control          Control         { get; private set; }
        public ID3D11Device     Device          { get; private set; }
        public bool             DisableRendering{ get; set; }
        public bool             Disposed        { get; private set; } = true;
        public Viewport         GetViewport     { get; private set; }
        public RendererInfo     Info            { get; internal set; }
        public int              MaxOffScreenTextures
                                                { get; set; } = 20;
        public VideoDecoder     VideoDecoder    { get; internal set; }
        public int              Zoom            { get => zoom; set { zoom = value; SetViewport(); if (!VideoDecoder.IsRunning) PresentFrame(); } }
        int zoom;

        //DeviceDebug                       deviceDbg;
        
        IDXGIFactory2                           factory;
        ID3D11DeviceContext1                    context;
        IDXGISwapChain1                         swapChain;

        ID3D11RenderTargetView                  rtv;
        ID3D11Texture2D                         backBuffer;
        
        // Used for off screen rendering
        ID3D11RenderTargetView[]                rtv2;
        ID3D11Texture2D[]                       backBuffer2;
        bool[]                                  backBuffer2busy;

        ID3D11SamplerState                      samplerLinear;

        ID3D11PixelShader                       pixelShader;

        ID3D11Buffer                            vertexBuffer;
        ID3D11InputLayout                       vertexLayout;
        ID3D11VertexShader                      vertexShader;

        ID3D11ShaderResourceView[]              curSRVs;
        ShaderResourceViewDescription           srvDescR, srvDescRG;

        static  InputElementDescription[]       inputElements =
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,     0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,        0),
            };

        static float[]                          vertexBufferData =
            {
                -1.0f,  -1.0f,  0,      0.0f, 1.0f,
                -1.0f,   1.0f,  0,      0.0f, 0.0f,
                 1.0f,  -1.0f,  0,      1.0f, 1.0f,
                
                 1.0f,  -1.0f,  0,      1.0f, 1.0f,
                -1.0f,   1.0f,  0,      0.0f, 0.0f,
                 1.0f,   1.0f,  0,      1.0f, 0.0f
            };

        static Renderer()
        {
            List<FeatureLevel> features = new List<FeatureLevel>();
            if (!Utils.IsWin7 && !Utils.IsWin8)
            {
                features.Add(FeatureLevel.Level_12_1);
                features.Add(FeatureLevel.Level_12_0);
            }

            if (!Utils.IsWin7)
                features.Add(FeatureLevel.Level_11_1);

            features.Add(FeatureLevel.Level_11_0);
            features.Add(FeatureLevel.Level_10_1);
            features.Add(FeatureLevel.Level_10_0);
            features.Add(FeatureLevel.Level_9_3);
            features.Add(FeatureLevel.Level_9_2);
            features.Add(FeatureLevel.Level_9_1);

            featureLevels = new FeatureLevel[features.Count -1];

            for (int i=0; i<features.Count-1; i++)
                featureLevels[i] = features[i];
        }

        static FeatureLevel[] featureLevels;

        FeatureLevel FeatureLevel;

        static Blob vsBlob;
        static Blob psBlob;

        // HDR to SDR
        ID3D11Buffer psBuffer;
        PSBufferType psBufferData = new PSBufferType();

        [StructLayout(LayoutKind.Sequential)]
        struct PSBufferType
        {
            // size needs to be multiple of 16

            public PSFormat format;
            public int coefsIndex;
            public PSHDR2SDRMethod hdrmethod;

            public float brightness;
            public float contrast;

            public float g_luminance;
            public float g_toneP1;
            public float g_toneP2;
        }
        enum PSFormat : int
        {
            RGB     = 1,
            Y_UV    = 2,
            Y_U_V   = 3
        }
        public enum PSHDR2SDRMethod : int
        {
            None    = 0,
            Aces    = 1,
            Hable   = 2,
            Reinhard= 3
        }

        public Renderer(VideoDecoder videoDecoder, Config config, Control control = null, int uniqueId = -1)
        {
            Config      = config;
            Control     = control;
            UniqueId    = uniqueId == -1 ? Utils.GetUniqueId() : uniqueId;
            VideoDecoder= videoDecoder;

            if (CreateDXGIFactory1(out factory).Failure)
                throw new InvalidOperationException("Cannot create IDXGIFactory1");

            using (IDXGIAdapter1 adapter = GetHardwareAdapter())
            {
                RendererInfo.Fill(this, adapter);
                Log("\r\n" + Info.ToString());

                DeviceCreationFlags creationFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

                #if DEBUG
                if (SdkLayersAvailable()) creationFlags |= DeviceCreationFlags.Debug;
                #endif

                if (D3D11CreateDevice(adapter, DriverType.Unknown, creationFlags, featureLevels, out ID3D11Device tempDevice, out FeatureLevel, out ID3D11DeviceContext tempContext).Failure)
                    D3D11CreateDevice(null,    DriverType.Warp,    creationFlags, featureLevels, out tempDevice, out FeatureLevel, out tempContext).CheckError();
                    // If the initialization fails, fall back to the WARP device. see http://go.microsoft.com/fwlink/?LinkId=286690

                Device = tempDevice. QueryInterface<ID3D11Device1>();
                context= tempContext.QueryInterface<ID3D11DeviceContext1>();
                tempContext.Dispose();
                tempDevice.Dispose();
            }
            
            using (var mthread    = Device.QueryInterface<ID3D11Multithread>()) mthread.SetMultithreadProtected(true);
            using (var dxgidevice = Device.QueryInterface<IDXGIDevice1>())      dxgidevice.MaximumFrameLatency = 1;

            if (Control != null)
            {
                Control.Resize += ResizeBuffers;

                SwapChainDescription1 swapChainDescription = new SwapChainDescription1()
                {
                    Format      = Format.B8G8R8A8_UNorm,
                    //Format      = Format.R10G10B10A2_UNorm,
                    Width       = Control.Width,
                    Height      = Control.Height,
                    AlphaMode   = AlphaMode.Ignore,
                    Usage       = Usage.RenderTargetOutput,

                    SampleDescription = new SampleDescription(1, 0)
                };

                SwapChainFullscreenDescription fullscreenDescription = new SwapChainFullscreenDescription
                {
                    Windowed = true
                };

                if (Utils.IsWin7)
                {
                    swapChainDescription.BufferCount = 1;
                    swapChainDescription.SwapEffect  = SwapEffect.Discard;
                }
                else if (Utils.IsWin8)
                {
                    swapChainDescription.BufferCount = 3;
                    swapChainDescription.SwapEffect  = SwapEffect.FlipSequential;
                }
                else // > Win 8
                {
                    swapChainDescription.BufferCount = 3;
                    swapChainDescription.SwapEffect  = SwapEffect.FlipSequential; // TBR: Ideally for Win10 FlipDiscard but having issues on fullscreen
                }

                swapChain   = factory.CreateSwapChainForHwnd(Device, Control.Handle, swapChainDescription, fullscreenDescription);
                backBuffer  = swapChain.GetBuffer<ID3D11Texture2D>(0);
                rtv         = Device.CreateRenderTargetView(backBuffer);

                GetViewport = new Viewport(0, 0, Control.Width, Control.Height);
                context.RSSetViewport(GetViewport);
            }

            vertexBuffer = Device.CreateBuffer(BindFlags.VertexBuffer, vertexBufferData);

            samplerLinear = Device.CreateSamplerState(new SamplerDescription()
            {
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                Filter   = Filter.MinMagMipLinear
            });

            // Compile VS/PS Embedded Resource Shaders (lock any static)
            lock (vertexBufferData)
            {
                System.Reflection.Assembly assembly = null;
                if (vsBlob == null)
                {
                    assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (Stream stream = assembly.GetManifestResourceStream(@"FlyleafLib.MediaFramework.MediaRenderer.Shaders.FlyleafVS.hlsl"))
                    {
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        Compiler.Compile(bytes, "main", null, "vs_4_0", out vsBlob, out Blob vsError);
                        if (vsError != null) Log(vsError.ConvertToString());
                    }
                }
            
                if (psBlob == null)
                {
                    if (assembly == null) assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (Stream stream = assembly.GetManifestResourceStream(@"FlyleafLib.MediaFramework.MediaRenderer.Shaders.FlyleafPS.hlsl"))
                    {
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        Compiler.Compile(bytes, "main", null, "ps_4_0", out psBlob, out Blob psError);
                        if (psError != null) Log(psError.ConvertToString());
                    }
                }
            }

            pixelShader  = Device.CreatePixelShader(psBlob);
            vertexLayout = Device.CreateInputLayout(inputElements, vsBlob);
            vertexShader = Device.CreateVertexShader(vsBlob);

            context.IASetInputLayout(vertexLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffers(0, new VertexBufferView(vertexBuffer, sizeof(float) * 5, 0));

            context.VSSetShader(vertexShader);
            context.PSSetShader(pixelShader);
            context.PSSetSampler(0, samplerLinear);

            psBuffer = Device.CreateBuffer(new BufferDescription() 
            {
                Usage           = ResourceUsage.Default,
                BindFlags       = BindFlags.ConstantBuffer,
                CpuAccessFlags  = CpuAccessFlags.None,
                SizeInBytes     = sizeof(PSBufferType)
            });
            context.PSSetConstantBuffer(0, psBuffer);

            Disposed = false;
        }
        private IDXGIAdapter1 GetHardwareAdapter()
        {
            IDXGIAdapter1 adapter = null;

            if (Config.Video.GPUAdapteLuid != -1)
            {
                for (int adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out adapter).Success; adapterIndex++)
                {
                    if (adapter.Description.Luid == Config.Video.GPUAdapteLuid)
                        return adapter;

                    adapter.Dispose();
                }

                throw new Exception($"GPU Adapter with {Config.Video.GPUAdapteLuid} has not been found");
            }
            
            IDXGIFactory6 factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
            if (factory6 != null)
            {
                for (int adapterIndex = 0; factory6.EnumAdapterByGpuPreference(adapterIndex, GpuPreference.HighPerformance, out adapter).Success; adapterIndex++)
                {
                    if (adapter == null)
                        continue;

                    if ((adapter.Description1.Flags & AdapterFlags.Software) != AdapterFlags.None)
                    {
                        adapter.Dispose();
                        continue;
                    }

                    return adapter;
                }

                factory6.Dispose();
            }

            if (adapter == null)
            {
                for (int adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out adapter).Success; adapterIndex++)
                {
                    if ((adapter.Description1.Flags & AdapterFlags.Software) != AdapterFlags.None)
                    {
                        adapter.Dispose();
                        continue;
                    }

                    return adapter;
                }
            }

            return adapter;
        }
        public static Dictionary<long, GPUAdapter> GetAdapters()
        {
            Dictionary<long, GPUAdapter> adapters = new Dictionary<long, GPUAdapter>();

            if (CreateDXGIFactory1(out IDXGIFactory2 factory).Failure)
                throw new InvalidOperationException("Cannot create IDXGIFactory1");

            #if DEBUG
            Utils.Log("GPU Adapters ...");
            #endif

            for (int adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
            {
                #if DEBUG
                Utils.Log($"[#{adapterIndex+1}] {adapter.Description.Description} ({adapter.Description.DeviceId})");
                #endif

                if ((adapter.Description1.Flags & AdapterFlags.Software) != AdapterFlags.None)
                {
                    adapter.Dispose();
                    continue;
                }

                bool hasOutput = false;
                adapter.EnumOutputs(0, out IDXGIOutput output);
                if (output != null)
                {
                    hasOutput = true;
                    output.Dispose();
                }

                adapters.Add(adapter.Description.Luid, new GPUAdapter() { Description = adapter.Description.Description, Luid = adapter.Description.Luid, HasOutput = hasOutput });

                adapter.Dispose();
                adapter = null;
            }

            factory.Dispose();

            return adapters;
        }
        public void Dispose()
        {
            if (Device == null) return;

            lock (Device)
            {
                if (Disposed) return;

                if (Control != null) Control.Resize -= ResizeBuffers;

                vertexShader.Dispose();
                samplerLinear.Dispose();
                vertexLayout.Dispose();
                vertexBuffer.Dispose();
                backBuffer.Dispose();
                rtv.Dispose();

                if (rtv2 != null)
                    for(int i=0; i<rtv2.Length-1; i++)
                        rtv2[i].Dispose();

                if (backBuffer2 != null)
                    for(int i=0; i<backBuffer2.Length-1; i++)
                        backBuffer2[i].Dispose();

                if (curSRVs != null) { for (int i=0; i<curSRVs.Length; i++) { curSRVs[i].Dispose(); curSRVs = null; } }

                
                context.ClearState();
                context.Flush();
                context.Dispose();
                Device.ImmediateContext.Dispose();
                swapChain.Dispose();
                factory.Dispose();
                
                Disposed = true;
            }

            #if DEBUG
            if (DXGIGetDebugInterface1(out IDXGIDebug1 dxgiDebug).Success)
            {
                dxgiDebug.ReportLiveObjects(DebugAll, ReportLiveObjectFlags.Summary | ReportLiveObjectFlags.IgnoreInternal);
                dxgiDebug.Dispose();
            }
            #endif

            vertexShader = null;
            vertexLayout = null;
            Device = null;
        }

        private void ResizeBuffers(object sender, EventArgs e)
        {
            if (Device == null) return;
            
            lock (Device)
            {
                rtv.Dispose();
                backBuffer.Dispose();

                swapChain.ResizeBuffers(swapChain.Description.BufferCount, Control.Width, Control.Height, Format.Unknown, SwapChainFlags.None);
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                rtv = Device.CreateRenderTargetView(backBuffer);

                SetViewport();
                PresentFrame(null);
            }
        }
        internal void FrameResized()
        {
            // TODO: Win7 doesn't support R8G8_UNorm so use SNorm will need also unormUV on pixel shader
            lock (Device)
            {
                srvDescR = new ShaderResourceViewDescription()
                {
                    Format = VideoDecoder.VideoStream.PixelBits > 8 ? Format.R16_UNorm : Format.R8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView()
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                srvDescRG = new ShaderResourceViewDescription()
                {
                    Format = VideoDecoder.VideoStream.PixelBits > 8 ? Format.R16G16_UNorm : Format.R8G8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView()
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                psBufferData.format = VideoDecoder.VideoAccelerated ? PSFormat.Y_UV : ((VideoDecoder.VideoStream.PixelFormatType == PixelFormatType.Software_Handled ? PSFormat.Y_U_V : PSFormat.RGB));                

                if (VideoDecoder.VideoStream.ColorSpace == "BT2020")
                    psBufferData.coefsIndex = 0;
                else if (VideoDecoder.VideoStream.ColorSpace == "BT709")
                    psBufferData.coefsIndex = 1;
                else if (VideoDecoder.VideoStream.ColorSpace == "BT601")
                    psBufferData.coefsIndex = 2;
                else
                    psBufferData.coefsIndex = 2;

                psBufferData.hdrmethod = VideoDecoder.VideoStream.ColorSpace == "BT2020" ? PSHDR2SDRMethod.Hable : PSHDR2SDRMethod.None;
                psBufferData.g_luminance = 400.0f;

                if (psBufferData.hdrmethod == PSHDR2SDRMethod.Reinhard)
                {
                    psBufferData.g_toneP1 = 0.72f;
                }
                else if (psBufferData.hdrmethod == PSHDR2SDRMethod.Aces)
                {
                    psBufferData.g_toneP1 = Config.Video.HDRtoSDRTone;
                }
                else if (psBufferData.hdrmethod == PSHDR2SDRMethod.Hable)
                {
                    psBufferData.g_toneP1 = (10000.0f / psBufferData.g_luminance) * (2.0f / Config.Video.HDRtoSDRTone);
                    psBufferData.g_toneP2 = psBufferData.g_luminance / (100.0f * Config.Video.HDRtoSDRTone);
                }
                

                psBufferData.contrast = Config.Video.Contrast / 100.0f;
                psBufferData.brightness = Config.Video.Brightness / 100.0f;

                context.UpdateSubresource(ref psBufferData, psBuffer);

                if (Control != null)
                    SetViewport();
                else
                {
                    if (rtv2 != null)
                        for (int i = 0; i < rtv2.Length - 1; i++)
                            rtv2[i].Dispose();

                    if (backBuffer2 != null)
                        for (int i = 0; i < backBuffer2.Length - 1; i++)
                            backBuffer2[i].Dispose();

                    backBuffer2busy = new bool[MaxOffScreenTextures];
                    rtv2 = new ID3D11RenderTargetView[MaxOffScreenTextures];
                    backBuffer2 = new ID3D11Texture2D[MaxOffScreenTextures];

                    for (int i = 0; i < MaxOffScreenTextures; i++)
                    {
                        backBuffer2[i] = Device.CreateTexture2D(new Texture2DDescription()
                        {
                            Usage       = ResourceUsage.Default,
                            BindFlags   = BindFlags.RenderTarget,
                            Format      = Format.B8G8R8A8_UNorm,
                            Width       = VideoDecoder.VideoStream.Width,
                            Height      = VideoDecoder.VideoStream.Height,

                            ArraySize   = 1,
                            MipLevels   = 1,
                            SampleDescription = new SampleDescription(1, 0)
                        });

                        rtv2[i] = Device.CreateRenderTargetView(backBuffer2[i]);
                    }

                    context.RSSetViewport(0, 0, VideoDecoder.VideoStream.Width, VideoDecoder.VideoStream.Height);
                }
            }
        }
        internal void FrameDisplayDataChanged(FFmpeg.AutoGen.AVMasteringDisplayMetadata* displayData)
        {
            if (psBufferData.hdrmethod != PSHDR2SDRMethod.None && displayData->has_luminance != 0)
            {
                if (psBufferData.hdrmethod == PSHDR2SDRMethod.Reinhard)
                {
                    psBufferData.g_toneP1 = (float) (Math.Log10(100) / Math.Log10(displayData->max_luminance.num / displayData->max_luminance.den));
                    if (psBufferData.g_toneP1 < 0.1f || psBufferData.g_toneP1 > 5.0f)
                        psBufferData.g_toneP1 = 0.72f;
                }
                else if (psBufferData.hdrmethod == PSHDR2SDRMethod.Aces)
                {
                    psBufferData.g_luminance = displayData->max_luminance.num / (float)displayData->max_luminance.den;
                    psBufferData.g_toneP1 = Config.Video.HDRtoSDRTone;
                }
                else if (psBufferData.hdrmethod == PSHDR2SDRMethod.Hable)
                {
                    psBufferData.g_luminance = displayData->max_luminance.num / (float)displayData->max_luminance.den;
                    psBufferData.g_toneP1 = (10000.0f / psBufferData.g_luminance) * (2.0f / Config.Video.HDRtoSDRTone);
                    psBufferData.g_toneP2 = psBufferData.g_luminance / (100.0f * Config.Video.HDRtoSDRTone);
                }

                context.UpdateSubresource(ref psBufferData, psBuffer);
            }
        }
        public void SetViewport()
        {
            if (Config.Video.AspectRatio == AspectRatio.Fill || (Config.Video.AspectRatio == AspectRatio.Keep && VideoDecoder.VideoStream == null))
            {
                GetViewport     = new Viewport(0, 0, Control.Width, Control.Height);
                context.RSSetViewport(GetViewport.X - zoom, GetViewport.Y - zoom, GetViewport.Width + (zoom * 2), GetViewport.Height + (zoom * 2));
            }
            else
            {
                float ratio = Config.Video.AspectRatio == AspectRatio.Keep ? VideoDecoder.VideoStream.AspectRatio.Value : (Config.Video.AspectRatio == AspectRatio.Custom ? Config.Video.CustomAspectRatio.Value : Config.Video.AspectRatio.Value);
                if (ratio <= 0) ratio = 1;

                if (Control.Width / ratio > Control.Height)
                {
                    GetViewport = new Viewport((int)(Control.Width - (Control.Height * ratio)) / 2, 0 ,(int) (Control.Height * ratio),Control.Height, 0.0f, 1.0f);
                    context.RSSetViewport(GetViewport.X - zoom, GetViewport.Y - zoom, GetViewport.Width + (zoom * 2), GetViewport.Height + (zoom * 2));
                }
                else
                {
                    GetViewport = new Viewport(0,(int)(Control.Height - (Control.Width / ratio)) / 2, Control.Width,(int) (Control.Width / ratio), 0.0f, 1.0f);
                    context.RSSetViewport(GetViewport.X - zoom, GetViewport.Y - zoom, GetViewport.Width + (zoom * 2), GetViewport.Height + (zoom * 2));
                }
            }
        }

        public bool PresentFrame(VideoFrame frame = null)
        {
            if (Device == null) return false;

            // Drop Frames | Priority on video frames
            bool gotIn = frame == null ? Monitor.TryEnter(Device, 1) : Monitor.TryEnter(Device, 5);

            if (gotIn)
            {
                if (rtv == null) return false;

                try
                {
                    if (frame != null)
                    {
                        if (VideoDecoder.VideoAccelerated)
                        {
                            curSRVs     = new ID3D11ShaderResourceView[2];
                            curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0], srvDescR);
                            curSRVs[1]  = Device.CreateShaderResourceView(frame.textures[0], srvDescRG);
                        }
                        else if (VideoDecoder.VideoStream.PixelFormatType == PixelFormatType.Software_Handled)
                        {
                            curSRVs     = new ID3D11ShaderResourceView[3];
                            curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0]);
                            curSRVs[1]  = Device.CreateShaderResourceView(frame.textures[1]);
                            curSRVs[2]  = Device.CreateShaderResourceView(frame.textures[2]);
                        }
                        else
                        {
                            curSRVs     = new ID3D11ShaderResourceView[1];
                            curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0]);
                        }

                        context.PSSetShaderResources(0, curSRVs);
                    }
                    
                    context.OMSetRenderTargets(rtv);
                    context.ClearRenderTargetView(rtv, Config.Video._BackgroundColor);
                    if (!DisableRendering) context.Draw(6, 0);
                    swapChain.Present(Config.Video.VSync, PresentFlags.None);
                    
                    if (frame != null)
                    {
                        if (frame.textures  != null)   for (int i=0; i<frame.textures.Length; i++) frame.textures[i].Dispose();
                        if (curSRVs         != null) { for (int i=0; i<curSRVs.Length; i++)      { curSRVs[i].Dispose(); } curSRVs = null; }
                    }

                } catch (Exception e) { Log($"Error {e.Message}"); // Currently seen on video switch when vframe (last frame of previous session) has different config from the new codec (eg. HW accel.)
                } finally { Monitor.Exit(Device); }

                return true;

            } else { Log("Dropped Frame - Lock timeout " + ( frame != null ? Utils.TicksToTime(frame.timestamp) : "")); VideoDecoder.DisposeFrame(frame); }

            return false;
        }

        public Bitmap GetBitmap(VideoFrame frame)
        {
            if (Device == null || frame == null) return null;

            int subresource = -1;

            ID3D11Texture2D stageTexture = Device.CreateTexture2D(new Texture2DDescription()
            {
	            Usage           = ResourceUsage.Staging,
	            ArraySize       = 1,
	            MipLevels       = 1,
	            Width           = backBuffer2[0].Description.Width,
	            Height          = backBuffer2[0].Description.Height,
	            Format          = Format.B8G8R8A8_UNorm,
	            BindFlags       = BindFlags.None,
	            CpuAccessFlags  = CpuAccessFlags.Read,
	            OptionFlags     = ResourceOptionFlags.None,
	            SampleDescription = new SampleDescription(1, 0)
            });

            lock (Device)
            {
                while (true)
                {
                    for (int i=0; i<MaxOffScreenTextures; i++)
                        if (!backBuffer2busy[i]) { subresource = i; break;}

                    if (subresource != -1)
                        break;
                    else
                        Thread.Sleep(5);
                }

                backBuffer2busy[subresource] = true;

                if (VideoDecoder.VideoAccelerated)
                {
                    curSRVs     = new ID3D11ShaderResourceView[2];
                    curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0], srvDescR);
                    curSRVs[1]  = Device.CreateShaderResourceView(frame.textures[0], srvDescRG);
                }
                else if (VideoDecoder.VideoStream.PixelFormatType == PixelFormatType.Software_Handled)
                {
                    curSRVs     = new ID3D11ShaderResourceView[3];
                    curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0]);
                    curSRVs[1]  = Device.CreateShaderResourceView(frame.textures[1]);
                    curSRVs[2]  = Device.CreateShaderResourceView(frame.textures[2]);
                }
                else
                {
                    curSRVs     = new ID3D11ShaderResourceView[1];
                    curSRVs[0]  = Device.CreateShaderResourceView(frame.textures[0]);
                }

                context.PSSetShaderResources(0, curSRVs);
                context.OMSetRenderTargets(rtv2[subresource]);
                //context.ClearRenderTargetView(rtv2[subresource], Config.video._ClearColor);
                context.Draw(6, 0);

                for (int i=0; i<frame.textures.Length; i++) frame.textures[i].Dispose();
                for (int i=0; i<curSRVs.Length; i++)      { curSRVs[i].Dispose(); } curSRVs = null;

                context.CopyResource(stageTexture, backBuffer2[subresource]);
                backBuffer2busy[subresource] = false;
            }

            return GetBitmap(stageTexture);
        }
        public Bitmap GetBitmap(ID3D11Texture2D stageTexture)
        {
            Bitmap bitmap   = new Bitmap(stageTexture.Description.Width, stageTexture.Description.Height);
            var db          = context.Map(stageTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var bitmapData  = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            
            if (db.RowPitch == bitmapData.Stride)
                MemoryHelpers.CopyMemory(bitmapData.Scan0, db.DataPointer, bitmap.Width * bitmap.Height * 4);
            else
            {
                var sourcePtr   = db.DataPointer;
                var destPtr     = bitmapData.Scan0;

                for (int y = 0; y < bitmap.Height; y++)
                {
                    MemoryHelpers.CopyMemory(destPtr, sourcePtr, bitmap.Width * 4);

                    sourcePtr   = IntPtr.Add(sourcePtr, db.RowPitch);
                    destPtr     = IntPtr.Add(destPtr, bitmapData.Stride);
                }
            }

            bitmap.UnlockBits(bitmapData);
            context.Unmap(stageTexture, 0);
            stageTexture.Dispose();
            
            return bitmap;
        }

        public void TakeSnapshot(string fileName)
        {
	        ID3D11Texture2D snapshotTexture;

	        lock (Device)
            {
                rtv.Dispose();
                backBuffer.Dispose();

                swapChain.ResizeBuffers(6, VideoDecoder.VideoStream.Width, VideoDecoder.VideoStream.Height, Format.Unknown, SwapChainFlags.None);
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                rtv = Device.CreateRenderTargetView(backBuffer);
                context.RSSetViewport(0, 0, backBuffer.Description.Width, backBuffer.Description.Height);

                for (int i=0; i<swapChain.Description.BufferCount; i++)
                { 
	                context.OMSetRenderTargets(rtv);
	                context.ClearRenderTargetView(rtv, Config.Video._BackgroundColor);
	                context.Draw(6, 0);
	                swapChain.Present(Config.Video.VSync, PresentFlags.None);
                }

                snapshotTexture = Device.CreateTexture2D(new Texture2DDescription()
                {
	                Usage           = ResourceUsage.Staging,
	                ArraySize       = 1,
	                MipLevels       = 1,
	                Width           = backBuffer.Description.Width,
	                Height          = backBuffer.Description.Height,
	                Format          = Format.B8G8R8A8_UNorm,
	                BindFlags       = BindFlags.None,
	                CpuAccessFlags  = CpuAccessFlags.Read,
	                OptionFlags     = ResourceOptionFlags.None,
	                SampleDescription = new SampleDescription(1, 0)         
                });
                context.CopyResource(snapshotTexture, backBuffer);
                ResizeBuffers(null, null);
            }

            Bitmap snapshotBitmap = GetBitmap(snapshotTexture);
            try { snapshotBitmap.Save(fileName, ImageFormat.Bmp); } catch (Exception) { }
            snapshotBitmap.Dispose();
        }

        private void Log(string msg) { Debug.WriteLine($"[{DateTime.Now.ToString("hh.mm.ss.fff")}] [#{UniqueId}] [Renderer] {msg}"); }
    }
}