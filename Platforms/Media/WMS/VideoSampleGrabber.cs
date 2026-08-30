using System;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;
using DX = SharpDX;


namespace Microsoft.Xna.Platform.Media
{
    internal class VideoSampleGrabber : DX.CallbackBase, SampleGrabberSinkCallback
    {
        internal byte[] TextureData { get; private set; }

        // Bumped once per genuinely new decoded sample (called on Media
        // Foundation's own work-queue thread, at the video's real frame
        // rate - e.g. ~24fps for a typical background clip) - lets
        // ConcreteVideoPlayerStrategy.PlatformGetTexture() (called on the
        // render thread, at the app's uncapped draw rate - measured 143Hz+
        // in this app) tell "new frame since last read" from "same frame
        // as last time" and skip the redundant GPU upload for the latter.
        // A plain long increment isn't fully synchronized with the
        // Marshal.Copy above (same pre-existing cross-thread relationship
        // TextureData itself already had), but a torn/stale read here
        // costs at most one frame of staleness on a decorative video, not
        // correctness - not worth a lock on this hot path.
        internal long FrameVersion { get; private set; }

        public void OnProcessSample(Guid guidMajorMediaType, int dwSampleFlags, long llSampleTime, long llSampleDuration, IntPtr sampleBufferRef, int dwSampleSize)
        {
            if (TextureData == null || TextureData.Length != dwSampleSize)
                TextureData = new byte[dwSampleSize];

            Marshal.Copy(sampleBufferRef, TextureData, 0, dwSampleSize);
            FrameVersion++;
        }

        public void OnSetPresentationClock(PresentationClock presentationClockRef)
        {

        }

        public void OnShutdown()
        {

        }

        public void OnClockPause(long systemTime)
        {

        }

        public void OnClockRestart(long systemTime)
        {

        }

        public void OnClockSetRate(long systemTime, float flRate)
        {

        }

        public void OnClockStart(long systemTime, long llClockStartOffset)
        {

        }

        public void OnClockStop(long hnsSystemTime)
        {

        }
    }
}
