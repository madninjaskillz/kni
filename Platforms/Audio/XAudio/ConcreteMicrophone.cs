// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework.Audio;
using SharpDX.MediaFoundation;

namespace Microsoft.Xna.Platform.Audio
{
    /// <summary>
    /// Provides microphones capture features.
    /// </summary>
    /// <remarks>
    /// Capture on this platform is MEDIA FOUNDATION, not XAudio2: XAudio2 is an
    /// output-only API and has no capture side at all, so the audio backend's
    /// name says nothing about where recorded audio can come from. Media
    /// Foundation is already a dependency of this platform (it is what the
    /// Media namespace plays songs and video with), so this adds no new
    /// binaries; it enumerates the same WASAPI endpoints the OS does.
    ///
    /// The Source Reader is asked for 16-bit mono PCM at
    /// <see cref="MicrophoneStrategy.SampleRate"/> and inserts the resampler
    /// and channel converter itself when the device does not natively speak
    /// that, which every device this has been tried on accepts - so this
    /// meets <see cref="Microphone"/>'s fixed contract (16-bit mono PCM at
    /// SampleRate) without a conversion pass here.
    ///
    /// Media Foundation's ReadSample BLOCKS, so a background thread owns the
    /// reader and fills a ring buffer that <see cref="PlatformGetData"/>
    /// drains. The ring is bounded: a caller that starts a capture and stops
    /// reading loses the OLDEST audio rather than growing without limit,
    /// which is also what an OpenAL capture device does when its buffer runs
    /// out from under it.
    /// </remarks>
    public sealed class ConcreteMicrophone : MicrophoneStrategy
    {
        private const int BitsPerSample = 16;
        private const int BytesPerSample = BitsPerSample / 8;

        private MediaSource _source;
        private SourceReader _reader;
        private Thread _thread;
        private volatile bool _running;

        // Ring buffer, guarded by _sync. Written by the reader thread,
        // drained by whoever calls GetData (the game thread, normally).
        private readonly object _sync = new object();
        private byte[] _ring;
        private int _writePos;
        private int _readPos;
        private int _queued;

        public override void PlatformStart(string deviceName)
        {
            if (_reader != null)
                return;

            MediaManager.Startup(true);

            Activate device = null;
            try
            {
                device = FindDevice(deviceName);
                if (device == null)
                    throw new NoMicrophoneConnectedException("No audio capture device is available.");

                _source = device.ActivateObject<MediaSource>();

                using (MediaAttributes readerAttributes = new MediaAttributes(1))
                    _reader = new SourceReader(_source, readerAttributes);

                _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                SetCaptureFormat(_reader, SampleRate);
            }
            catch (Exception ex)
            {
                Cleanup();
                MediaManager.Shutdown();
                if (ex is NoMicrophoneConnectedException)
                    throw;
                throw new NoMicrophoneConnectedException("Failed to open the audio capture device. " + ex.Message);
            }
            finally
            {
                if (device != null)
                    device.Dispose();
            }

            // Four buffer-durations of slack, and never less than a second:
            // enough that a caller reading once per BufferReady cannot lose
            // audio to a late frame, and small enough to stay a rounding
            // error in memory (a second of 16-bit mono at 44.1 kHz is 88 KB).
            int bufferBytes = GetSampleSizeInBytes(BufferDuration);
            _ring = new byte[Math.Max(bufferBytes * 4, SampleRate * BytesPerSample)];
            _writePos = 0;
            _readPos = 0;
            _queued = 0;

            _running = true;
            _thread = new Thread(ReadLoop);
            _thread.IsBackground = true;
            _thread.Name = "Microphone capture";
            _thread.Start();
        }

        public override void PlatformStop()
        {
            _running = false;

            if (_thread != null)
            {
                // ReadSample returns within one sample duration (~10 ms) of
                // the flag being cleared. If it somehow does not, leave the
                // reader alone rather than disposing it under the thread -
                // a leaked device is recoverable, a torn-down COM object
                // being read from is not.
                if (!_thread.Join(1000))
                {
                    _thread = null;
                    return;
                }
                _thread = null;
            }

            Cleanup();
            MediaManager.Shutdown();

            lock (_sync)
            {
                _ring = null;
                _writePos = 0;
                _readPos = 0;
                _queued = 0;
            }
        }

        public override bool PlatformIsHeadset()
        {
            // Windows exposes no such distinction: a USB headset's microphone
            // is an ordinary capture endpoint. Same answer as every other
            // desktop platform gives.
            return false;
        }

        public override bool PlatformUpdate()
        {
            lock (_sync)
            {
                return _queued > 0;
            }
        }

        public override int PlatformGetData(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                if (_ring == null || _queued <= 0)
                    return 0;

                // Whole samples only: handing back half of a 16-bit sample
                // would byte-shift everything that followed it.
                int take = Math.Min(count, _queued);
                take -= take % BytesPerSample;
                if (take <= 0)
                    return 0;

                int firstRun = Math.Min(take, _ring.Length - _readPos);
                Buffer.BlockCopy(_ring, _readPos, buffer, offset, firstRun);
                if (take > firstRun)
                    Buffer.BlockCopy(_ring, 0, buffer, offset + firstRun, take - firstRun);

                _readPos = (_readPos + take) % _ring.Length;
                _queued -= take;
                return take;
            }
        }

        private void ReadLoop()
        {
            while (_running)
            {
                Sample sample = null;
                try
                {
                    int actualStreamIndex;
                    SourceReaderFlags flags;
                    long timestamp;
                    sample = _reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlags.None,
                                                out actualStreamIndex, out flags, out timestamp);

                    if (sample == null)
                    {
                        // No sample and end-of-stream means the device went
                        // away (unplugged, or taken exclusively by something
                        // else); anything else is just a gap.
                        if ((flags & SourceReaderFlags.Endofstream) != 0)
                            break;
                        continue;
                    }

                    using (MediaBuffer mediaBuffer = sample.ConvertToContiguousBuffer())
                    {
                        int maxLength, currentLength;
                        IntPtr data = mediaBuffer.Lock(out maxLength, out currentLength);
                        try
                        {
                            Append(data, currentLength);
                        }
                        finally
                        {
                            mediaBuffer.Unlock();
                        }
                    }
                }
                catch (Exception)
                {
                    // A device removed mid-capture throws out of ReadSample.
                    // Stop reading; Stop()/Start() is the recovery, and the
                    // queued audio stays readable in the meantime.
                    break;
                }
                finally
                {
                    if (sample != null)
                        sample.Dispose();
                }
            }
        }

        private void Append(IntPtr data, int length)
        {
            if (length <= 0)
                return;

            lock (_sync)
            {
                if (_ring == null)
                    return;

                // A block bigger than the whole ring can only keep its tail.
                int offset = 0;
                if (length > _ring.Length)
                {
                    offset = length - _ring.Length;
                    length = _ring.Length;
                }

                int firstRun = Math.Min(length, _ring.Length - _writePos);
                Marshal.Copy(data + offset, _ring, _writePos, firstRun);
                if (length > firstRun)
                    Marshal.Copy(data + offset + firstRun, _ring, 0, length - firstRun);

                _writePos = (_writePos + length) % _ring.Length;
                _queued += length;

                // Overrun: the write head has passed the read head, so the
                // oldest audio is gone. Move the read head to the oldest
                // sample that survives rather than reporting samples that
                // have been overwritten.
                if (_queued > _ring.Length)
                {
                    _queued = _ring.Length;
                    _readPos = _writePos;
                }
            }
        }

        private void Cleanup()
        {
            if (_reader != null)
            {
                try { _reader.Dispose(); } catch (Exception) { }
                _reader = null;
            }
            if (_source != null)
            {
                try { _source.Dispose(); } catch (Exception) { }
                _source = null;
            }
        }

        private static void SetCaptureFormat(SourceReader reader, int sampleRate)
        {
            using (MediaType mediaType = new MediaType())
            {
                MediaFactory.CreateMediaType(mediaType);
                mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                mediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, sampleRate);
                mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, 1);
                mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, BitsPerSample);
                mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, BytesPerSample);
                mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, sampleRate * BytesPerSample);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, mediaType);
            }
        }

        /// <summary>
        /// The capture endpoints Media Foundation can see, as
        /// (friendly name, is-default) pairs, in enumeration order. Called by
        /// ConcreteAudioService to populate <see cref="Microphone.All"/>.
        /// </summary>
        internal static List<KeyValuePair<string, bool>> EnumerateDevices()
        {
            List<KeyValuePair<string, bool>> result = new List<KeyValuePair<string, bool>>();

            MediaManager.Startup(true);
            try
            {
                string defaultEndpointId = GetDefaultEndpointId();

                Activate[] devices = EnumerateActivates();
                try
                {
                    for (int i = 0; i < devices.Length; i++)
                    {
                        string name = TryGetString(devices[i], CaptureDeviceAttributeKeys.FriendlyName);
                        if (string.IsNullOrEmpty(name))
                            continue;

                        string endpointId = TryGetString(devices[i], CaptureDeviceAttributeKeys.SourceTypeAudcapEndpointId);

                        // No role query available (or no match): the first
                        // device enumerated is the least-wrong default.
                        bool isDefault = (defaultEndpointId != null)
                            ? (endpointId == defaultEndpointId)
                            : (result.Count == 0);

                        result.Add(new KeyValuePair<string, bool>(name, isDefault));
                    }
                }
                finally
                {
                    for (int i = 0; i < devices.Length; i++)
                        devices[i].Dispose();
                }
            }
            catch (Exception)
            {
                // No capture devices, or Media Foundation unavailable: an
                // empty list, exactly as an OpenAL build reports when the
                // capture extension is missing.
            }
            finally
            {
                MediaManager.Shutdown();
            }

            return result;
        }

        private static Activate[] EnumerateActivates()
        {
            using (MediaAttributes attributes = new MediaAttributes(1))
            {
                attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeAudioCapture.Guid);
                return MediaFactory.EnumDeviceSources(attributes);
            }
        }

        /// <summary>The endpoint id of the console default recording device,
        /// or null when the role query is not answered.</summary>
        private static string GetDefaultEndpointId()
        {
            try
            {
                using (MediaAttributes attributes = new MediaAttributes(2))
                {
                    attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeAudioCapture.Guid);
                    attributes.Set(CaptureDeviceAttributeKeys.SourceTypeAudcapRole, 0); // eConsole
                    Activate[] devices = MediaFactory.EnumDeviceSources(attributes);
                    try
                    {
                        if (devices.Length > 0)
                            return TryGetString(devices[0], CaptureDeviceAttributeKeys.SourceTypeAudcapEndpointId);
                    }
                    finally
                    {
                        for (int i = 0; i < devices.Length; i++)
                            devices[i].Dispose();
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>
        /// The device a <see cref="Microphone"/> names, or the first one when
        /// the name is null/unknown. Names come from EnumerateDevices, so a
        /// match is the norm; two devices sharing a friendly name resolve to
        /// the first, which is the same ambiguity the OpenAL path has.
        /// </summary>
        private static Activate FindDevice(string deviceName)
        {
            Activate[] devices = EnumerateActivates();
            if (devices.Length == 0)
                return null;

            // Unnamed, or named something that is no longer plugged in: the
            // first device. Failing outright would turn "the headset you
            // recorded with last time is gone" into a dead record button.
            int chosen = 0;
            if (!string.IsNullOrEmpty(deviceName))
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (TryGetString(devices[i], CaptureDeviceAttributeKeys.FriendlyName) == deviceName)
                    {
                        chosen = i;
                        break;
                    }
                }
            }

            for (int i = 0; i < devices.Length; i++)
            {
                if (i != chosen)
                    devices[i].Dispose();
            }

            return devices[chosen];
        }

        private static string TryGetString(MediaAttributes attributes, MediaAttributeKey<string> key)
        {
            try
            {
                return attributes.Get(key);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
