// Copyright (C)2026 - Raw GLSL effect pipeline extension.
//
// Lets a KNI game ship hand-authored GLSL directly for OpenGL-family
// targets (DesktopGL, BlazorGL/WebGL2) instead of routing HLSL through
// MojoShader, which only ever understands SM2/3-class bytecode (a hard
// ceiling independent of GraphicsProfile - see RawGlslEffectProcessor's
// own header for the full story). This content type is the parsed form
// of a ".glslfx" manifest: one shared vertex shader plus N named
// techniques, each a single pixel shader, all hand-written GLSL text.

using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    /// <summary>A single scalar/vector uniform, packed by hand into the
    /// effect's one constant buffer at a caller-chosen byte offset (must
    /// stay 4-byte aligned and non-overlapping - there is no HLSL-style
    /// packer here, the manifest author owns the layout).</summary>
    public class RawGlslParameterInfo
    {
        public string Name;

        /// <summary>One of "float", "vec2", "vec3", "vec4".</summary>
        public string Type;

        public int Offset;
    }

    /// <summary>A texture unit binding - Slot must match whatever the
    /// caller sets via GraphicsDevice.Textures[Slot] before drawing.</summary>
    public class RawGlslSamplerInfo
    {
        public int Slot;
        public string Name;
    }

    /// <summary>One named technique: a single pass, single pixel shader,
    /// sharing the effect's one vertex shader.</summary>
    public class RawGlslTechniqueInfo
    {
        public string Name;
        public string PixelShaderSource;
    }

    public class RawGlslEffectContent : ContentItem
    {
        public string VertexShaderSource;

        public List<RawGlslParameterInfo> Parameters = new List<RawGlslParameterInfo>();
        public List<RawGlslSamplerInfo> Samplers = new List<RawGlslSamplerInfo>();
        public List<RawGlslTechniqueInfo> Techniques = new List<RawGlslTechniqueInfo>();
    }
}
