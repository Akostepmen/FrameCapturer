﻿using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;


namespace UTJ
{
    [AddComponentMenu("UTJ/FrameCapturer/GifRecorder")]
    [RequireComponent(typeof(Camera))]
    public class GifRecorder : IMovieRecorder
    {
        public enum FrameRateMode
        {
            Variable,
            Constant,
        }

        [Tooltip("output directory. filename is generated automatically.")]
        public DataPath m_outputDir = new DataPath(DataPath.Root.PersistentDataPath, "");
        public int m_resolutionWidth = 300;
        public int m_numColors = 256;
        public FrameRateMode m_frameRateMode = FrameRateMode.Constant;
        [Tooltip("relevant only if FrameRateMode is Constant")]
        public int m_framerate = 30;
        public int m_captureEveryNthFrame = 2;
        public int m_keyframe = 30;
        [Tooltip("0 is treated as processor count")]
        public int m_maxTasks = 0;
        public Shader m_sh_copy;

        string m_output_file;
        fcAPI.fcGIFContext m_ctx;
        Material m_mat_copy;
        Mesh m_quad;
        CommandBuffer m_cb;
        RenderTexture m_scratch_buffer;
        int m_num_frames;
        bool m_recording = false;


        void UpdateScratchBuffer()
        {
            var cam = GetComponent<Camera>();
            int capture_width = m_resolutionWidth;
            int capture_height = (int)((float)m_resolutionWidth / ((float)cam.pixelWidth / (float)cam.pixelHeight));

            if (m_scratch_buffer != null)
            {
                // update is not needed
                if (m_scratch_buffer.IsCreated() &&
                    m_scratch_buffer.width == capture_width && m_scratch_buffer.height == capture_height)
                {
                    return;
                }
                else
                {
                    ReleaseScratchBuffer();
                }
            }

            m_scratch_buffer = new RenderTexture(capture_width, capture_height, 0, RenderTextureFormat.ARGB32);
            m_scratch_buffer.wrapMode = TextureWrapMode.Repeat;
            m_scratch_buffer.Create();
        }

        void ReleaseScratchBuffer()
        {
            if (m_scratch_buffer != null)
            {
                m_scratch_buffer.Release();
                m_scratch_buffer = null;
            }
        }

        public override bool IsSeekable() { return true; }
        public override bool IsEditable() { return true; }

        public override bool BeginRecording()
        {
            if(m_ctx.ptr != IntPtr.Zero) { return false; }

            UpdateScratchBuffer();

            m_num_frames = 0;
            if (m_maxTasks <= 0)
            {
                m_maxTasks = SystemInfo.processorCount;
            }
            fcAPI.fcGifConfig conf;
            conf.width = m_scratch_buffer.width;
            conf.height = m_scratch_buffer.height;
            conf.num_colors = Mathf.Clamp(m_numColors, 1, 255);
            conf.max_active_tasks = m_maxTasks;
            m_ctx = fcAPI.fcGifCreateContext(ref conf);

            GetComponent<Camera>().AddCommandBuffer(CameraEvent.AfterEverything, m_cb);

            Debug.Log("GifRecorder.BeginRecording()");
            return true;
        }

        public override bool EndRecording()
        {
            if (m_ctx.ptr == IntPtr.Zero) { return false; }
            m_recording = false;

            GetComponent<Camera>().RemoveCommandBuffer(CameraEvent.AfterEverything, m_cb);

            fcAPI.fcGifDestroyContext(m_ctx);
            m_ctx.ptr = IntPtr.Zero;

            Debug.Log("GifRecorder.EndRecording()");
            return true;
        }

        public override bool recording
        {
            get { return m_recording; }
            set { m_recording = value; }
        }

        public override string GetOutputPath()
        {
            return m_outputDir.GetPath() + "/" + m_output_file;
        }

        public override bool Flush()
        {
            return Flush(0, -1);
        }

        public override bool Flush(int begin_frame, int end_frame)
        {
            bool ret = false;
            if (m_ctx.ptr != IntPtr.Zero && m_num_frames > 0)
            {
                m_output_file = DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gif";
                var path = GetOutputPath();
                ret = fcAPI.fcGifWriteFile(m_ctx, path, begin_frame, end_frame);
                Debug.Log("GifRecorder.FlushFile(" + begin_frame + ", " + end_frame + "): " + path);
            }
            return ret;
        }

        public override RenderTexture GetScratchBuffer() { return m_scratch_buffer; }

        public override void EraseFrame(int begin_frame, int end_frame)
        {
            fcAPI.fcGifEraseFrame(m_ctx, begin_frame, end_frame);
        }

        public override int GetExpectedFileSize(int begin_frame = 0, int end_frame = -1)
        {
            return fcAPI.fcGifGetExpectedDataSize(m_ctx, begin_frame, end_frame);
        }

        public override int GetFrameCount()
        {
            return fcAPI.fcGifGetFrameCount(m_ctx);
        }

        public override void GetFrameData(RenderTexture rt, int frame)
        {
            fcAPI.fcGifGetFrameData(m_ctx, rt.GetNativeTexturePtr(), frame);
        }

        public fcAPI.fcGIFContext GetGifContext() { return m_ctx; }


#if UNITY_EDITOR
        void Reset()
        {
            m_sh_copy = FrameCapturerUtils.GetFrameBufferCopyShader();
        }

        void OnValidate()
        {
            m_numColors = Mathf.Clamp(m_numColors, 1, 255);
        }
#endif // UNITY_EDITOR

        void OnEnable()
        {
            m_outputDir.CreateDirectory();
            m_quad = FrameCapturerUtils.CreateFullscreenQuad();
            m_mat_copy = new Material(m_sh_copy);
            if (GetComponent<Camera>().targetTexture != null)
            {
                m_mat_copy.EnableKeyword("OFFSCREEN");
            }

            {
                int tid = Shader.PropertyToID("_TmpFrameBuffer");
                m_cb = new CommandBuffer();
                m_cb.name = "GifRecorder: copy frame buffer";
                m_cb.GetTemporaryRT(tid, -1, -1, 0, FilterMode.Point);
                m_cb.Blit(BuiltinRenderTextureType.CurrentActive, tid);
                // tid は意図的に開放しない
            }
        }

        void OnDisable()
        {
            EndRecording();
            ReleaseScratchBuffer();

            if (m_cb != null) {
                m_cb.Release();
                m_cb = null;
            }
        }

        IEnumerator OnPostRender()
        {
            if (m_recording)
            {
                yield return new WaitForEndOfFrame();

                if (Time.frameCount % m_captureEveryNthFrame == 0)
                {
                    bool keyframe = m_keyframe > 0 && m_num_frames % m_keyframe == 0;
                    double timestamp = -1.0;
                    if(m_frameRateMode == FrameRateMode.Constant)
                    {
                        timestamp = 1.0 / m_framerate * m_num_frames;
                    }

                    m_mat_copy.SetPass(0);
                    Graphics.SetRenderTarget(m_scratch_buffer);
                    Graphics.DrawMeshNow(m_quad, Matrix4x4.identity);
                    Graphics.SetRenderTarget(null);
                    fcAPI.fcGifAddFrameTexture(m_ctx, m_scratch_buffer, keyframe, timestamp);

                    m_num_frames++;
                }
            }
        }
    }

}