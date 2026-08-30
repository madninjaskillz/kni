// Copyright (C)2026 - Raw GLSL effect pipeline extension.
//
// WHY THIS EXISTS
// ================
// EffectProcessor (see Processors/EffectProcessor.cs) always compiles the
// pass's HLSL down to DX9-class (SM2/3) bytecode via fxc, then hands that
// bytecode to MojoShader to translate into GLSL - see ShaderProfileGL.cs.
// MojoShader has never understood anything beyond SM2/3 bytecode, which
// is a hard ceiling (~512 arithmetic instruction slots once a compute-
// heavy shader like a raymarcher is forced through it) completely
// independent of GraphicsProfile/Reach-vs-HiDef. That ceiling is why this
// game's raymarched vis shaders were previously DirectX-only.
//
// The fix isn't at the KNI *runtime* level at all - ConcreteShader.
// CreateShader (Platforms/Graphics/.GL/Shader/ConcreteShader.cs) already
// just treats the shader "bytecode" as literal ASCII GLSL source text and
// hands it to GL.ShaderSource/GL.CompileShader as-is. Nothing about the
// runtime cares how that text was produced. So this processor builds the
// exact same on-disk Effect binary format EffectProcessor does (reusing
// its EffectObject/ShaderData/ConstantBufferData model and
// EffectObjectWriter verbatim, unmodified), but populates it from
// hand-written GLSL files instead of running HLSL through fxc+MojoShader.
// A real GLSL 330 core (or GLSL ES 300, for BlazorGL) compile has no
// MojoShader-derived instruction ceiling at all - it's compiled by the
// actual GL driver.
//
// RUNTIME CONTRACT this processor must honor (reverse-engineered from the
// .GL backend, see ConcreteConstantBuffer.PlatformApply/ConcreteGraphics-
// Context.CreateProgram/ConcreteVertexShader.GetAttributeLocation):
//   - Constant buffers are applied as ONE glUniform4fv call per buffer,
//     against a uniform the GLSL declares as "uniform vec4 <Name>[N];" -
//     there is no per-field reflection at runtime, so the manifest's
//     param offsets are load-bearing: they must match how the GLSL
//     indexes into that array.
//   - Vertex attributes are bound post-link via glGetAttribLocation(name)
//     and matched to the app's VertexDeclaration purely by
//     (VertexElementUsage, UsageIndex) - the GLSL attribute's own name is
//     free-form as long as ShaderData.Attribute.name matches the "in"
//     declaration.
//   - Sampler uniforms are bound to a texture unit post-link via
//     glGetUniformLocation(GLsamplerName) + glUniform1i(unit) - again
//     free-form naming, just needs to match the GLSL "uniform sampler2D"
//     declaration.
//   - Vertex shaders must end by writing gl_Position and then applying
//     KNI's "posFixup" GL/D3D coordinate-convention correction - see
//     FullScreenVS.vert for the canonical shared vertex shader.
//
// MANIFEST GRAMMAR (.glslfx, read by RawGlslEffectImporter):
//   vertex: <path to shared .vert file>
//   param: <Name> <float|vec2|vec3|vec4> <byteOffset>
//   sampler: <textureUnit> <Name>
//   technique: <TechniqueName> <path to .frag file>
// One "param" line per uniform; all params are packed into a single
// constant buffer named "Params". Offsets are chosen by the manifest
// author (see VisEffectsGL.glslfx) - this processor does not attempt
// HLSL-style automatic packing.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework.Content.Pipeline.EffectCompiler;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    [ContentProcessor(DisplayName = "Raw GLSL Effect - KNI")]
    public class RawGlslEffectProcessor : ContentProcessor<RawGlslEffectContent, CompiledEffectContent>
    {
        private const string ConstantBufferName = "Params";
        private const string PositionAttributeName = "Position";
        private const string TexCoordAttributeName = "TexCoord";

        public override CompiledEffectContent Process(RawGlslEffectContent input, ContentProcessorContext context)
        {
            bool isGles;
            switch (context.TargetPlatform)
            {
                case TargetPlatform.DesktopGL:
                    isGles = false;
                    break;
                case TargetPlatform.BlazorGL:
                    isGles = true;
                    break;
                default:
                    throw new InvalidContentException(
                        string.Format("RawGlslEffectProcessor only supports DesktopGL/BlazorGL targets, not {0}.", context.TargetPlatform),
                        input.Identity);
            }

            EffectObject effect = new EffectObject();
            effect.ConstantBuffers = new List<ConstantBufferData>();
            effect.Shaders = new List<ShaderData>();

            bool hasParams = input.Parameters.Count > 0;
            if (hasParams)
                effect.ConstantBuffers.Add(BuildConstantBuffer(input));

            ShaderData vsShader = new ShaderData(ShaderStage.Vertex, effect.Shaders.Count);
            vsShader.ShaderCode = Encoding.ASCII.GetBytes(BuildVertexGlsl(input.VertexShaderSource, isGles));
            vsShader._attributes = new ShaderData.Attribute[]
            {
                MakeAttribute(PositionAttributeName, VertexElementUsage.Position, 0),
                MakeAttribute(TexCoordAttributeName, VertexElementUsage.TextureCoordinate, 0),
            };
            vsShader._samplers = new SamplerInfo[0];
            vsShader._cbuffers = new int[0];
            vsShader.ShaderFunctionName = "__RawGlslVS";
            vsShader.ShaderProfile = "raw_glsl";
            effect.Shaders.Add(vsShader);

            effect.Techniques = new EffectObject.EffectTechniqueContent[input.Techniques.Count];
            for (int t = 0; t < input.Techniques.Count; t++)
            {
                RawGlslTechniqueInfo tinfo = input.Techniques[t];

                ShaderData psShader = new ShaderData(ShaderStage.Pixel, effect.Shaders.Count);
                psShader.ShaderCode = Encoding.ASCII.GetBytes(BuildPixelGlsl(tinfo.PixelShaderSource, isGles));
                psShader._samplers = BuildSamplers(input);
                psShader._cbuffers = hasParams ? new int[] { 0 } : new int[0];
                psShader._attributes = new ShaderData.Attribute[0];
                psShader.ShaderFunctionName = "__RawGlslPS_" + tinfo.Name;
                psShader.ShaderProfile = "raw_glsl";
                effect.Shaders.Add(psShader);

                EffectObject.EffectPassContent pass = new EffectObject.EffectPassContent();
                pass.name = "P0";
                pass.blendState = null;
                pass.depthStencilState = null;
                pass.rasterizerState = null;
                pass.states = new EffectObject.EffectStateContent[]
                {
                    MakeShaderState(ShaderStage.Pixel, psShader.SharedIndex),
                    MakeShaderState(ShaderStage.Vertex, vsShader.SharedIndex),
                };
                pass.state_count = (uint)pass.states.Length;

                EffectObject.EffectTechniqueContent technique = new EffectObject.EffectTechniqueContent();
                technique.name = tinfo.Name;
                technique.pass_count = 1;
                technique.pass_handles = new EffectObject.EffectPassContent[] { pass };

                effect.Techniques[t] = technique;
            }

            effect.Parameters = BuildFlatParameterList(effect);

            return WriteEffect(effect);
        }

        // ---- constant buffer / parameters --------------------------------

        private static ConstantBufferData BuildConstantBuffer(RawGlslEffectContent input)
        {
            ConstantBufferData cbuffer = new ConstantBufferData(ConstantBufferName);

            int maxEnd = 0;
            for (int i = 0; i < input.Parameters.Count; i++)
            {
                RawGlslParameterInfo p = input.Parameters[i];

                EffectObject.EffectParameterContent param = new EffectObject.EffectParameterContent();
                param.name = p.Name;
                param.semantic = string.Empty;
                param.bufferOffset = p.Offset;
                SetParameterShape(param, p.Type, input.Identity);
                param.data = new byte[param.rows * param.columns * 4];
                param.member_handles = new EffectObject.EffectParameterContent[0];

                cbuffer.Parameters.Add(param);
                cbuffer.ParameterOffset.Add(p.Offset);

                int end = p.Offset + (int)(param.rows * param.columns * 4);
                if (end > maxEnd)
                    maxEnd = end;
            }

            // The runtime uploads the WHOLE buffer as one vec4[] uniform
            // (GL.Uniform4 over BufferData.Length/16 vectors) and asserts
            // the byte length is itself a multiple of 16 - round up.
            cbuffer.Size = ((maxEnd + 15) / 16) * 16;

            return cbuffer;
        }

        private static void SetParameterShape(EffectObject.EffectParameterContent param, string type, ContentIdentity identity)
        {
            param.type = EffectObject.PARAMETER_TYPE.FLOAT;
            switch (type)
            {
                case "float":
                    param.class_ = EffectObject.PARAMETER_CLASS.SCALAR;
                    param.rows = 1;
                    param.columns = 1;
                    break;
                case "vec2":
                    param.class_ = EffectObject.PARAMETER_CLASS.VECTOR;
                    param.rows = 1;
                    param.columns = 2;
                    break;
                case "vec3":
                    param.class_ = EffectObject.PARAMETER_CLASS.VECTOR;
                    param.rows = 1;
                    param.columns = 3;
                    break;
                case "vec4":
                    param.class_ = EffectObject.PARAMETER_CLASS.VECTOR;
                    param.rows = 1;
                    param.columns = 4;
                    break;
                default:
                    throw new InvalidContentException("Unknown param type '" + type + "' (expected float/vec2/vec3/vec4).", identity);
            }
        }

        private static SamplerInfo[] BuildSamplers(RawGlslEffectContent input)
        {
            SamplerInfo[] samplers = new SamplerInfo[input.Samplers.Count];
            for (int i = 0; i < input.Samplers.Count; i++)
            {
                RawGlslSamplerInfo s = input.Samplers[i];

                SamplerInfo si = new SamplerInfo();
                si.type = MojoShader.SamplerType.SAMPLER_2D;
                si.textureSlot = s.Slot;
                si.samplerSlot = s.Slot;
                si.GLsamplerName = s.Name;
                si.textureName = s.Name;
                si.textureParameter = -1;
                si.state = null;

                samplers[i] = si;
            }
            return samplers;
        }

        private static ShaderData.Attribute MakeAttribute(string name, VertexElementUsage usage, int index)
        {
            ShaderData.Attribute attrib = new ShaderData.Attribute();
            attrib.name = name;
            attrib.usage = usage;
            attrib.index = index;
            return attrib;
        }

        // Mirrors EffectProcessor.CreateShader's EffectStateContent shape
        // exactly (operation 146/147 are the magic "this pass's vertex/
        // pixel shader is shader index N" markers EffectObject.
        // GetShaderIndex/state_table look for) - not reachable from here
        // since it's internal to EffectProcessor, so reproduced verbatim.
        private static EffectObject.EffectStateContent MakeShaderState(ShaderStage stage, int sharedShaderIndex)
        {
            EffectObject.EffectStateContent state = new EffectObject.EffectStateContent();
            state.index = 0;
            state.type = EffectObject.STATE_TYPE.CONSTANT;
            state.operation = (stage == ShaderStage.Vertex) ? (uint)146 : (uint)147;

            state.parameter = new EffectObject.EffectParameterContent();
            state.parameter.name = string.Empty;
            state.parameter.semantic = string.Empty;
            state.parameter.class_ = EffectObject.PARAMETER_CLASS.OBJECT;
            state.parameter.type = (stage == ShaderStage.Vertex)
                                 ? EffectObject.PARAMETER_TYPE.VERTEXSHADER
                                 : EffectObject.PARAMETER_TYPE.PIXELSHADER;
            state.parameter.rows = 0;
            state.parameter.columns = 0;
            state.parameter.data = sharedShaderIndex;

            return state;
        }

        // Mirrors EffectProcessor.CompileEffect's flat-parameter-list pass
        // (constant buffer params first, then one synthetic Texture2D
        // parameter per distinct sampler texture name) - same reason as
        // MakeShaderState above: the original is private to EffectProcessor.
        private static EffectObject.EffectParameterContent[] BuildFlatParameterList(EffectObject effect)
        {
            List<EffectObject.EffectParameterContent> parameters = new List<EffectObject.EffectParameterContent>();

            for (int c = 0; c < effect.ConstantBuffers.Count; c++)
            {
                ConstantBufferData cb = effect.ConstantBuffers[c];
                for (int i = 0; i < cb.Parameters.Count; i++)
                {
                    EffectObject.EffectParameterContent param = cb.Parameters[i];
                    int match = parameters.FindIndex(delegate(EffectObject.EffectParameterContent e) { return e.name == param.name; });
                    if (match == -1)
                    {
                        cb.ParameterIndex.Add(parameters.Count);
                        parameters.Add(param);
                    }
                    else
                    {
                        cb.ParameterIndex.Add(match);
                    }
                }
            }

            foreach (ShaderData shader in effect.Shaders)
            {
                for (int s = 0; s < shader._samplers.Length; s++)
                {
                    SamplerInfo samplerInfo = shader._samplers[s];
                    string textureName = samplerInfo.textureName;

                    int match = parameters.FindIndex(delegate(EffectObject.EffectParameterContent e) { return e.name == textureName; });
                    if (match == -1)
                    {
                        shader._samplers[s].textureParameter = parameters.Count;

                        EffectObject.EffectParameterContent param = new EffectObject.EffectParameterContent();
                        param.class_ = EffectObject.PARAMETER_CLASS.OBJECT;
                        param.name = textureName;
                        param.semantic = string.Empty;
                        param.type = EffectObject.PARAMETER_TYPE.TEXTURE2D;
                        param.member_handles = new EffectObject.EffectParameterContent[0];
                        parameters.Add(param);
                    }
                    else
                    {
                        shader._samplers[s].textureParameter = match;
                    }
                }
            }

            return parameters.ToArray();
        }

        // ---- GLSL wrapping -------------------------------------------------

        // DesktopGL: ConcreteShader.CreateShader compiles the bytes exactly
        // as given (no post-processing) unless GraphicsProfile>=HiDef AND
        // the backend is GLES - so a real "#version 330 core" source is
        // used byte-for-byte here.
        //
        // BlazorGL (GLES): that same code path rewrites attribute/varying/
        // gl_FragColor GLSL-ES-100-style source up to "#version 300 es"
        // for us (ConvertGLES100ToGLES300) - so for that target we author
        // ES-300-compatible source (in/out, texture()) but deliberately
        // omit our own #version line and let KNI prepend one, matching
        // what every other KNI effect on that platform already goes
        // through.
        private static string BuildVertexGlsl(string source, bool isGles)
        {
            if (isGles)
                return source;
            return "#version 330 core\n" + source;
        }

        private static string BuildPixelGlsl(string source, bool isGles)
        {
            if (isGles)
                return source;
            return "#version 330 core\n" + source;
        }

        // ---- serialization (identical shape to EffectProcessor.Write,
        // which is private to that class) ------------------------------

        private const string MGFXHeader = "MGFX";
        private const int Version = 10;

        private static CompiledEffectContent WriteEffect(EffectObject effect)
        {
            using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
            {
                using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write(MGFXHeader.ToCharArray());
                    writer.Write((byte)Version);
                    writer.Write((byte)ShaderProfileType.OpenGL_Mojo);

                    using (System.IO.MemoryStream memStream = new System.IO.MemoryStream())
                    using (EffectObjectWriter memWriter = new EffectObjectWriter(memStream, Version, ShaderProfileType.OpenGL_Mojo))
                    {
                        memWriter.WriteEffect(effect);

                        int effectKey = MonoGame.Framework.Utilities.Hash.ComputeHash(memStream);
                        writer.Write((Int32)effectKey);

                        memStream.WriteTo(writer.BaseStream);
                    }

                    writer.Write(MGFXHeader.ToCharArray());
                }

                return new CompiledEffectContent(stream.ToArray());
            }
        }
    }
}
