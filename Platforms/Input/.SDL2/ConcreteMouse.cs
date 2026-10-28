// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

// Copyright (C)2021-2024 Nick Kastellanos

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Microsoft.Xna.Platform.Input
{
    public sealed class ConcreteMouse : MouseStrategy
    {
        private IntPtr _wndHandle = IntPtr.Zero;

        private Sdl SDL { get { return Sdl.Current; } }

        private int RawX;
        private int RawY;
        internal int ScrollX;
        internal int ScrollY;

        public override IntPtr PlatformGetWindowHandle()
        {
            return _wndHandle;
        }

        public override void PlatformSetWindowHandle(IntPtr windowHandle)
        {
            _wndHandle = windowHandle;
        }

        public override bool PlatformIsRawInputAvailable()
        {
            return true;
        }

        public override MouseState PlatformGetState()
        {
            Point mousePos;
            Point relPos;
            Sdl.Mouse.Button state;
            IntPtr wndHandle = _wndHandle;
            if (wndHandle != IntPtr.Zero)
            {
                SdlGameWindow gameWindow = SdlGameWindow.FromHandle(wndHandle);

                int winFlags = SDL.WINDOW.GetWindowFlags(wndHandle);

                switch (gameWindow._sysWMType)
                {
                    case Sdl.Window.SysWMType.Wayland:
                        {
                            if (SDL.SDLInitThreadId == SDL.GetManagedThreadId())
                                SDL.PumpEvents();
                            state = SDL.MOUSE.GetState(out mousePos.X, out mousePos.Y);
                        }
                        break;
                    default:
                        {
#if ENABLE_TOUCHINPUT
                            if (SDL.SDLInitThreadId == SDL.GetManagedThreadId())
                                SDL.PumpEvents();
                            state = SDL.MOUSE.GetState(out mousePos.X, out mousePos.Y);
#else
                            Point globalPos, windowPos;
                            state = SDL.MOUSE.GetGlobalState(out globalPos.X, out globalPos.Y);
                            SDL.WINDOW.GetPosition(wndHandle, out windowPos.X, out windowPos.Y);
                            mousePos = globalPos - windowPos;
#endif
                        }
                        break;
                }

                // fissure/ezmuze: SDL reports points; the window reports its size in
                // pixels on a high-DPI window (SdlGameWindow.IsHighDpi), and
                // the two have to agree or every click lands at half distance.
                float scale = (gameWindow != null) ? gameWindow.BackingScale : 1f;
                if (scale != 1f)
                {
                    mousePos.X = (int)Math.Round(mousePos.X * scale);
                    mousePos.Y = (int)Math.Round(mousePos.Y * scale);
                }
            }
            else // (wndHandle == IntPtr.Zero)
            {
                state = SDL.MOUSE.GetGlobalState(out mousePos.X, out mousePos.Y);
            }

            SDL.MOUSE.GetRelativeState(out relPos.X, out relPos.Y);
            unchecked
            {
                RawX += relPos.X;
                RawY += relPos.Y;
            }

            MouseState mouseState = new MouseState(
                x: mousePos.X, y: mousePos.Y,
                scrollWheel: ScrollY, horizontalScrollWheel: ScrollX,
                rawX: RawX, rawY: RawY,
                leftButton:   (state & Sdl.Mouse.Button.Left)   != 0 ? ButtonState.Pressed : ButtonState.Released,
                middleButton: (state & Sdl.Mouse.Button.Middle) != 0 ? ButtonState.Pressed : ButtonState.Released,
                rightButton:  (state & Sdl.Mouse.Button.Right)  != 0 ? ButtonState.Pressed : ButtonState.Released,
                xButton1: (state & Sdl.Mouse.Button.X1Mask) != 0 ? ButtonState.Pressed : ButtonState.Released,
                xButton2: (state & Sdl.Mouse.Button.X2Mask) != 0 ? ButtonState.Pressed : ButtonState.Released
                );

            return mouseState;
        }

        public override void PlatformSetPosition(int x, int y)
        {
            SdlGameWindow gameWindow = SdlGameWindow.FromHandle(_wndHandle);

            float scale = gameWindow.BackingScale;
            SDL.MOUSE.WarpInWindow(gameWindow.Handle, (int)Math.Round(x / scale), (int)Math.Round(y / scale));
        }

        public override void PlatformSetCursor(MouseCursor cursor)
        {
            SDL.MOUSE.SetCursor(cursor.Handle);
        }

    }
}
