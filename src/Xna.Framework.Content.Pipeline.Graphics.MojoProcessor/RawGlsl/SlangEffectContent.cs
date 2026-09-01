// Copyright (C)2026 - Slang effect pipeline extension.
//
// Parsed form of a ".slangfx" manifest: ONE Slang source file, plus the
// technique/entry-point mapping and vertex-attribute usages that Slang itself
// has no way to express.
//
// Everything else — the constant buffer's fields, their byte offsets, their
// shapes — comes from Slang's own reflection at build time, NOT from this
// manifest. That is the whole point: hand-written offsets are how the GLSL and
// the C# that feeds it drift apart, and std140 does not pack the way a person
// guesses (a vec2 after a float lands at +8, not +4).

using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    /// <summary>One named technique: a vertex and a fragment entry point in
    /// the shared Slang source.</summary>
    public class SlangTechniqueInfo
    {
        public string Name;
        public string VertexEntryPoint;
        public string FragmentEntryPoint;
    }

    public class SlangEffectContent : ContentItem
    {
        /// <summary>Absolute path to the .slang file — the compilers are
        /// external processes and want a path, not text.</summary>
        public string SourcePath;

        /// <summary>
        /// Vertex attributes, as (GLSL name, VertexElementUsage, index).
        ///
        /// Still declared by hand, because what binds an attribute at draw time
        /// is (usage, index) matched against the app's VertexDeclaration, and
        /// that mapping is an app-side convention Slang knows nothing about.
        /// Four lines that change about never, against a constant-buffer layout
        /// that changes whenever a uniform is added — which is why one is
        /// declared and the other is reflected.
        /// </summary>
        public List<RawGlslAttributeInfo> Attributes = new List<RawGlslAttributeInfo>();

        public List<SlangTechniqueInfo> Techniques = new List<SlangTechniqueInfo>();

        /// <summary>
        /// fxc profiles for the D3D output's technique block, which Slang has
        /// no concept of. Defaulted to the Reach-safe pair this pipeline has
        /// always used; a manifest can raise them.
        /// </summary>
        public string VertexProfile = "vs_4_0_level_9_1";

        public string PixelProfile = "ps_4_0_level_9_1";
    }
}
