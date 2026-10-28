# KNI 4.3.9001, patched for Fissure's macOS build

Upstream **KNI v4.3.9001** (tag `v4.3.9001`, commit `7944bebff`; the released packages were
built from `88726b31`, whose `src/` and `Platforms/` are identical). Licence: **Ms-PL**.

Only `nkast.Kni.Platform.SDL2.GL` is rebuilt, as **4.3.9001.1-fissure.1**. Its assembly
version stays 4.3.9001.0 and it depends on the stock `nkast.Xna.Framework.*` 4.3.9001
packages. Every change is gated on macOS (`CurrentPlatform.OS == OS.MacOSX`), so Windows and
Linux run exactly the upstream code paths.

Ported from ezmuze/kni (which patched KNI 3.14.9001), adapted to 4.3's layout:

- **Core Profile GL context** (ezmuze `3cb8646c`). macOS honours KNI's GL 2.1 request
  literally, so GLSL 330 does not compile. `Graphics/.GL.SDL/OpenGL.SDL.cs`
  (`OGL_SDL.SetContextVersion`, `IsCoreProfile`) asks for 3.2 Core + forward-compatible in
  the adapter probe (`ConcreteGraphicsAdapter.cs`) and the device
  (`Game/.SDL2/ConcreteGraphicsDeviceManager.cs`); the DEBUG context flag keeps
  forward-compatible (`ConcreteGraphicsDevice.SDL.cs`). Core Profile consequences:
  - `Graphics/.GL/OpenGL.cs` — `InitExtensions` falls back to `glGetStringi`.
  - `Graphics/.GL.SDL/ConcreteGraphicsContext.SDL.cs` — binds one vertex array object for
    the context's lifetime.
  - `Graphics/.GL/ConcreteGraphicsContext.cs` — the three `DrawUser*Primitives` overloads
    stream through context-owned buffer objects instead of client-side arrays.
- **Retina** (ezmuze `61575f0e`). `Game/.SDL2/SDLGameWindow.cs` creates the window with
  `AllowHighDPI` and keeps its size (so `ClientBounds` and the backbuffer) in drawable
  pixels, converting to points only for SDL calls that take points;
  `Input/.SDL2/ConcreteMouse.cs` scales by `SdlGameWindow.BackingScale`;
  `Game/.SDL2/SDL2.GL.cs` binds `SDL_GL_GetDrawableSize`.
- **Dock icon** (ezmuze `bb341b83`). `SDLGameWindow.RecreateWindow` does not call
  `SDL_SetWindowIcon` on macOS, where it would replace the bundle's `.icns`.

Build: `git submodule update --init --depth 1`, then
`dotnet pack Platforms/Kni.Platform.SDL2.GL.csproj -c Release`.
