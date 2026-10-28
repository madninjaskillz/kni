// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Platform.Utilities;

namespace Microsoft.Xna.Platform.Graphics.OpenGL
{
    internal sealed class OGL_SDL : OGL
    {
        public static void Initialize()
        {
            System.Diagnostics.Debug.Assert(OGL._current == null);
            OGL._current = new OGL_SDL();
        }

        private OGL_SDL() : base()
        {
        }

        /// <summary>
        /// fissure/ezmuze: sets the context version SDL will create, and on macOS asks
        /// for a Core Profile instead.
        ///
        /// Every other desktop driver treats the legacy 2.x request as a floor
        /// and hands back a compatibility context at its highest version, so
        /// GLSL 330 compiles there. macOS takes the request literally: a 2.x
        /// request gets exactly GL 2.1 / GLSL 1.20, and "#version 330" fails
        /// with "version '330' is not supported". macOS only offers GL 3+ as a
        /// Core Profile (3.2 Core is answered with 4.1 on every current Mac),
        /// and Core requires forward-compatible on macOS.
        ///
        /// Core Profile has three further consequences handled elsewhere:
        /// glGetString(GL_EXTENSIONS) is gone (OGL.InitExtensions), a vertex
        /// array object must be bound to draw (ConcreteGraphicsContext), and
        /// client-side vertex arrays are gone, which every DrawUser* call used
        /// (ConcreteGraphicsContextGL.StreamUserVertices).
        /// </summary>
        internal static void SetContextVersion(int major, int minor)
        {
            if (IsCoreProfile)
            {
                Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextMajorVersion, 3);
                Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextMinorVersion, 2);
                Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextProfileMask, (int)Sdl.GL.ContextProfile.Core);
                Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextFlags, (int)Sdl.GL.ContextFlag.ForwardCompatible);
                return;
            }

            Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextMajorVersion, major);
            Sdl.Current.OpenGL.SetAttribute(Sdl.GL.Attribute.ContextMinorVersion, minor);
        }

        /// <summary>fissure/ezmuze: true where <see cref="SetContextVersion"/> asks for a Core Profile (macOS only).</summary>
        internal static bool IsCoreProfile
        {
            get { return CurrentPlatform.OS == OS.MacOSX; }
        }


        protected override IntPtr GetNativeLibrary()
        {
            BoundApi = RenderApi.GL;

            return IntPtr.Zero;
        }

        protected override T LoadFunction<T>(string function)
        {
            IntPtr funcAddress = Sdl.Current.OpenGL.GetProcAddress(function);

            if (funcAddress != IntPtr.Zero)
                return InteropHelpers.GetDelegateForFunctionPointer<T>(funcAddress);

            throw new EntryPointNotFoundException("Entry point not found for function '" + function + "'.");
        }

        protected override T LoadFunctionOrNull<T>(string function)
        {
            IntPtr funcAddress = Sdl.Current.OpenGL.GetProcAddress(function);

            if (funcAddress != IntPtr.Zero)
                return InteropHelpers.GetDelegateForFunctionPointer<T>(funcAddress);

            return default(T);
        }

    }
}

