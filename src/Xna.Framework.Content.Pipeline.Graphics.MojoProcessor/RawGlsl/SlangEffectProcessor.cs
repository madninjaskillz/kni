// Copyright (C)2026 - Slang effect pipeline extension.
//
// WHY THIS EXISTS
// ================
// RawGlslEffectProcessor (beside this file) lets a GL target ship real GLSL
// instead of HLSL routed through MojoShader's SM2/3-class ceiling. It works,
// and it costs a second hand-written source per shader: the DirectX/Xbox path
// still needs HLSL, so the same shader exists twice and a divergence renders
// wrongly on ONE platform only.
//
// This processor removes that. ONE .slang source is compiled by slangc, and
// the GLSL is GENERATED - nobody edits it, so there is nothing to keep in
// step. The same source can produce the HLSL for the D3D path too.
//
// THE PIPELINE, and why every hop is there:
//
//   .slang --slangc -target spirv--> SPIR-V --spirv-cross--> GLSL
//
// Slang emits GLSL directly, and it is the wrong GLSL for us: its own target
// list calls it "GLSL(Vulkan)". It is #version 450 with std140 uniform BLOCKS,
// -profile glsl_330 does not change that, and there is no GLSL-ES target at
// all. WebGL2 is ES 3.0 and cannot compile 450 under any circumstances.
// spirv-cross is what retargets it: --version 330 / --es --version 300, and
// --flatten-ubo, which turns the uniform block into exactly the flat
// "uniform vec4 Params[N]" this runtime already uploads with one glUniform4fv
// (see RawGlslEffectProcessor's runtime-contract header). With that flag the
// generated GLSL needs no runtime change whatsoever.
//
// THREE THINGS THE GENERATOR HAS TO FIX UP, all found by running it:
//
//  1. VARYING NAMES DO NOT MATCH ACROSS STAGES. spirv-cross names vertex
//     outputs "entryPointParam_<entry>_<field>" and fragment inputs
//     "input_<field>", and GLSL 330 links varyings BY NAME. Both sides are
//     renamed by location to one agreed scheme. Renaming a location that does
//     not exist is tolerated, so a blanket 0..MaxVaryingLocations is safe.
//  2. THE UNIFORM ARRAY NAME. spirv-cross calls it after the Slang parameter
//     group; the runtime looks it up by the constant buffer's name, which
//     RawGlslEffectProcessor fixes as "Params".
//  3. posFixup. KNI's GL-vs-D3D correction, normally appended by MojoShader
//     after translation. Slang knows nothing about it, so it is appended here.
//
// OFFSETS COME FROM REFLECTION, NEVER FROM A MANIFEST. slangc -reflection-json
// gives every field's byte offset, and those are std140 offsets that do not
// match what a person would guess: a vec2 following a float lands at +8, not
// +4. Hand-writing them is how the GLSL and the C# that feeds it drift apart.
//
// TOOLS. slangc and spirv-cross are external, and dev-machine-only: compiled
// .xnb are committed and the release build only consumes them, exactly as the
// existing content pipeline already assumes (it cannot run fxc on Linux
// either). Discovery order is an explicit env var, then PATH, then the Vulkan
// SDK for spirv-cross.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    [ContentProcessor(DisplayName = "Slang Effect - KNI")]
    public class SlangEffectProcessor : ContentProcessor<SlangEffectContent, CompiledEffectContent>
    {
        /// <summary>How many varying locations to blanket-rename. Renaming a
        /// location that is not there is a no-op in spirv-cross, so this only
        /// has to be an upper bound; 16 is the classic GL varying limit.</summary>
        private const int MaxVaryingLocations = 16;

        private const string VaryingPrefix = "slang_varying_";

        public override CompiledEffectContent Process(SlangEffectContent input, ContentProcessorContext context)
        {
            string slangc = FindTool("SLANGC", "slangc.exe", null, input.Identity,
                "Slang (https://github.com/shader-slang/slang/releases) - set SLANGC to slangc.exe, or put it on PATH.");

            // D3D wants HLSL, and gets it from the SAME .slang. That is the
            // whole point of this processor existing rather than a second
            // hand-written source: DirectX (and an eventual Xbox build, which
            // cannot be OpenGL at all) is a first-class output here, not the
            // thing the GL path is working around.
            if (context.TargetPlatform == TargetPlatform.Windows)
                return ProcessHlsl(input, context, slangc);

            bool isGles;
            switch (context.TargetPlatform)
            {
                case TargetPlatform.DesktopGL: isGles = false; break;
                case TargetPlatform.BlazorGL: isGles = true; break;
                default:
                    throw new InvalidContentException(
                        string.Format("SlangEffectProcessor supports Windows/DesktopGL/BlazorGL targets, not {0}.", context.TargetPlatform),
                        input.Identity);
            }

            string spirvCross = FindTool("SPIRV_CROSS", "spirv-cross.exe", "VULKAN_SDK", input.Identity,
                "SPIRV-Cross - install the Vulkan SDK (it ships Bin/spirv-cross.exe), or set SPIRV_CROSS.");

            string temp = Path.Combine(Path.GetTempPath(), "kni-slang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                var generated = new RawGlslEffectContent();
                generated.Identity = input.Identity;
                generated.Attributes = input.Attributes;

                // Reflection once, from the first technique's vertex entry: the
                // constant buffer is module scope, shared by every entry point.
                generated.Parameters = ReflectParameters(
                    slangc, input.SourcePath, input.Techniques[0].VertexEntryPoint, temp, input.Identity);

                bool first = true;
                foreach (SlangTechniqueInfo technique in input.Techniques)
                {
                    string vs = Generate(slangc, spirvCross, input.SourcePath,
                        technique.VertexEntryPoint, "vertex", isGles, temp, input.Identity, input.Attributes);
                    string ps = Generate(slangc, spirvCross, input.SourcePath,
                        technique.FragmentEntryPoint, "fragment", isGles, temp, input.Identity, input.Attributes);

                    // ONE shared vertex shader, which is RawGlslEffectContent's
                    // shape. Every technique here uses the same vertex entry in
                    // practice; if one ever does not, this is the line that has
                    // to grow rather than something silently using the wrong one.
                    if (first)
                    {
                        generated.VertexShaderSource = AppendPosFixup(vs, input.Identity);
                        first = false;
                    }
                    else if (AppendPosFixup(vs, input.Identity) != generated.VertexShaderSource)
                    {
                        throw new InvalidContentException(
                            "Every technique in a .slangfx must share one vertex entry point; '" + technique.Name +
                            "' generated a different vertex shader.", input.Identity);
                    }

                    generated.Techniques.Add(new RawGlslTechniqueInfo
                    {
                        Name = technique.Name,
                        PixelShaderSource = ps,
                    });

                    // DECLARED samplers win outright. Detection can only
                    // assign units by declaration order, and order is not slot
                    // -- see SlangEffectContent.Samplers.
                    if (input.Samplers.Count > 0)
                    {
                        generated.Samplers = input.Samplers;
                    }
                    else
                    {
                        foreach (string sampler in FindSamplers(ps))
                        {
                            if (!generated.Samplers.Exists(s => s.Name == sampler))
                            {
                                generated.Samplers.Add(new RawGlslSamplerInfo
                                {
                                    Slot = generated.Samplers.Count,
                                    Name = sampler,
                                });
                            }
                        }
                    }
                }

                return RawGlslEffectProcessor.Build(generated, context);
            }
            finally
            {
                try { Directory.Delete(temp, true); }
                catch (Exception) { }
            }
        }

        // ---- HLSL / D3D ---------------------------------------------------

        /// <summary>
        /// The D3D path: slangc emits HLSL, this assembles a .fx from it, and
        /// the STOCK EffectProcessor compiles that exactly as it always has.
        /// Nothing about the D3D pipeline changes -- fxc, the profiles, the
        /// EffectObject, all identical. Only the authorship moves.
        ///
        /// TWO THINGS HAVE TO BE FIXED UP, and both are why this cannot just
        /// hand slangc's output straight to fxc:
        ///
        /// 1. SLANG WRAPS THE CBUFFER CONTENTS IN A STRUCT --
        ///    "cbuffer Parameters { SLANG_... Parameters; }" -- even when the
        ///    uniforms are declared as loose globals. D3D reflection then
        ///    reports ONE parameter of struct type, so
        ///    effect.Parameters["MatrixTransform"] is null and every caller
        ///    that sets a uniform by name throws. The struct is inlined and the
        ///    "Parameters." prefix dropped.
        /// 2. SLANGC REFUSES ONE -o FOR TWO ENTRY POINTS and emits a complete,
        ///    self-contained file per entry, so the vertex and pixel outputs
        ///    have to be merged -- shared declarations once, both entry
        ///    functions, then the technique block a .fx needs and Slang has no
        ///    concept of.
        ///
        /// Both are deterministic text transforms over generated input, and
        /// both fail LOUDLY: if either gets it wrong, fxc rejects the result at
        /// build time rather than something rendering oddly later.
        /// </summary>
        private static CompiledEffectContent ProcessHlsl(
            SlangEffectContent input, ContentProcessorContext context, string slangc)
        {
            string temp = Path.Combine(Path.GetTempPath(), "kni-slang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                var fx = new StringBuilder();
                var techniques = new StringBuilder();
                string baseText = null;

                foreach (SlangTechniqueInfo technique in input.Techniques)
                {
                    string vs = FlattenCBuffer(
                        GenerateHlsl(slangc, input.SourcePath, technique.VertexEntryPoint, "vertex", temp, input.Identity),
                        input.Identity);
                    string ps = FlattenCBuffer(
                        GenerateHlsl(slangc, input.SourcePath, technique.FragmentEntryPoint, "fragment", temp, input.Identity),
                        input.Identity);

                    if (baseText == null)
                        baseText = vs;
                    else
                        baseText = MergeInto(baseText, vs, technique.VertexEntryPoint, input.Identity);

                    baseText = MergeInto(baseText, ps, technique.FragmentEntryPoint, input.Identity);

                    techniques.Append("technique ").Append(technique.Name).Append("\n{\n    pass P0\n    {\n");
                    techniques.Append("        VertexShader = compile ").Append(input.VertexProfile)
                        .Append(' ').Append(technique.VertexEntryPoint).Append("();\n");
                    techniques.Append("        PixelShader = compile ").Append(input.PixelProfile)
                        .Append(' ').Append(technique.FragmentEntryPoint).Append("();\n");
                    techniques.Append("    }\n}\n\n");
                }

                fx.Append(baseText).Append("\n\n").Append(techniques);

                // A real file on disk: EffectProcessor preprocesses relative to
                // its SourceFilename, so it needs somewhere to resolve from.
                string fxPath = Path.Combine(temp, Path.GetFileNameWithoutExtension(input.SourcePath) + ".fx");
                File.WriteAllText(fxPath, fx.ToString());

                var effect = new EffectContent();
                effect.Identity = new ContentIdentity(fxPath);
                effect.EffectCode = fx.ToString();

                return new EffectProcessor().Process(effect, context);
            }
            finally
            {
                try { Directory.Delete(temp, true); }
                catch (Exception) { }
            }
        }

        private static string GenerateHlsl(
            string slangc, string source, string entryPoint, string stage, string temp, ContentIdentity identity)
        {
            string outPath = Path.Combine(temp, entryPoint + ".hlsl");

            // -no-mangle, or every uniform arrives as "MatrixTransform_0" and
            // no caller can find it by the name it wrote in the .slang.
            Run(slangc, Quote(source) + " -target hlsl -no-mangle -entry " + entryPoint
                + " -stage " + stage + " -o " + Quote(outPath), identity, "slangc (hlsl)");

            return File.ReadAllText(outPath);
        }

        /// <summary>
        /// Turns "cbuffer P : register(b0) { SomeStruct P; }" into a cbuffer
        /// holding the struct's fields directly, and drops the now-redundant
        /// "P." prefix from every use. See ProcessHlsl for why.
        /// </summary>
        private static string FlattenCBuffer(string hlsl, ContentIdentity identity)
        {
            var cbuffer = new System.Text.RegularExpressions.Regex(
                @"cbuffer\s+(\w+)\s*:\s*register\(b\d+\)\s*\{\s*(\w+)\s+(\w+);\s*\}");

            System.Text.RegularExpressions.Match m = cbuffer.Match(hlsl);
            if (!m.Success)
            {
                // No constant buffer at all, or one that is already flat. Both
                // are fine; nothing to do.
                return hlsl;
            }

            string name = m.Groups[1].Value;
            string structType = m.Groups[2].Value;
            string variable = m.Groups[3].Value;

            var structRegex = new System.Text.RegularExpressions.Regex(
                @"struct\s+" + structType + @"\s*\{(?<body>[^}]*)\};",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            System.Text.RegularExpressions.Match sm = structRegex.Match(hlsl);
            if (!sm.Success)
            {
                throw new InvalidContentException(
                    "Generated HLSL declares cbuffer '" + name + "' as struct '" + structType
                    + "', but that struct's body was not found to inline.", identity);
            }

            string flattened = "cbuffer " + name + " : register(b0)\n{"
                + sm.Groups["body"].Value.TrimEnd() + "\n}";

            hlsl = hlsl.Substring(0, m.Index) + flattened + hlsl.Substring(m.Index + m.Length);
            return hlsl.Replace(variable + ".", string.Empty);
        }

        /// <summary>
        /// Folds one generated HLSL file into another: every top-level
        /// definition the base does not already have.
        ///
        /// This has to be GENERAL, not "structs, resources and the entry
        /// point". slangc emits a complete, self-contained file per entry
        /// point, so the second file carries every helper ITS entry calls --
        /// and with nine entry points sharing a raymarcher, that is most of the
        /// shader. Copying only the entry function produced HLSL that
        /// referenced functions and uniforms nobody had declared, which fxc
        /// reported as "undeclared identifier" a hundred lines from the cause.
        ///
        /// Dedupe is by NAME, which is sound here because both files come from
        /// the same module compiled with the same flags: a shared helper is
        /// byte-identical in both, so keeping the first copy loses nothing.
        /// </summary>
        private static string MergeInto(string baseText, string other, string entryPoint, ContentIdentity identity)
        {
            var result = new StringBuilder(baseText);

            foreach (KeyValuePair<string, string> definition in TopLevelDefinitions(other))
            {
                if (!DeclaresName(baseText, definition.Key) && !DeclaresName(result.ToString(), definition.Key))
                    result.Append("\n\n").Append(definition.Value);
            }

            if (!DeclaresName(result.ToString(), entryPoint))
                result.Append("\n\n").Append(ExtractFunction(other, entryPoint, identity));

            return result.ToString();
        }

        private static bool DeclaresName(string hlsl, string name)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                hlsl, @"(?:struct|cbuffer)\s+" + name + @"\b")
                || System.Text.RegularExpressions.Regex.IsMatch(
                    hlsl, @"^[^\r\n/#][^\r\n(]*\b" + name + @"\s*\(",
                    System.Text.RegularExpressions.RegexOptions.Multiline)
                || System.Text.RegularExpressions.Regex.IsMatch(
                    hlsl, @"\b" + name + @"\s*:\s*register\(")
                || System.Text.RegularExpressions.Regex.IsMatch(
                    hlsl, @"^\s*static\s+[^\r\n]*\b" + name + @"\s*;",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        /// <summary>
        /// Every top-level definition in a generated HLSL file, as
        /// (name, text). Walks brace depth rather than pattern-matching whole
        /// constructs, because a function body contains everything a top-level
        /// construct looks like.
        /// </summary>
        private static List<KeyValuePair<string, string>> TopLevelDefinitions(string hlsl)
        {
            var found = new List<KeyValuePair<string, string>>();
            int i = 0;

            while (i < hlsl.Length)
            {
                // Skip whitespace, preprocessor lines and comments between
                // definitions. #line directives are dense in Slang's output.
                while (i < hlsl.Length && char.IsWhiteSpace(hlsl[i]))
                    i++;

                if (i >= hlsl.Length)
                    break;

                if (hlsl[i] == '#' || (i + 1 < hlsl.Length && hlsl[i] == '/' && hlsl[i + 1] == '/'))
                {
                    int eol = hlsl.IndexOf('\n', i);
                    if (eol < 0)
                        break;

                    i = eol + 1;
                    continue;
                }

                int begin = i;
                int depth = 0;
                bool sawBrace = false;

                while (i < hlsl.Length)
                {
                    char c = hlsl[i];
                    if (c == '{')
                    {
                        depth++;
                        sawBrace = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;

                            // A struct/cbuffer ends "};" - take the semicolon.
                            while (i < hlsl.Length && (hlsl[i] == ' ' || hlsl[i] == '\r' || hlsl[i] == '\n'))
                                i++;

                            if (i < hlsl.Length && hlsl[i] == ';')
                                i++;

                            break;
                        }
                    }
                    else if (c == ';' && depth == 0 && !sawBrace)
                    {
                        // A plain declaration: a resource, a static global.
                        i++;
                        break;
                    }

                    i++;
                }

                string text = hlsl.Substring(begin, Math.Min(i, hlsl.Length) - begin).Trim();
                if (text.Length == 0)
                    continue;

                string name = DefinitionName(text);
                if (name != null)
                    found.Add(new KeyValuePair<string, string>(name, text));
            }

            return found;
        }

        /// <summary>The declared name of one top-level definition, or null when
        /// it is something this does not need to carry across.</summary>
        private static string DefinitionName(string text)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                text, @"^(?:struct|cbuffer)\s+(?<name>\w+)");
            if (m.Success)
                return m.Groups["name"].Value;

            // A function: the identifier immediately before the parameter list.
            m = System.Text.RegularExpressions.Regex.Match(text, @"(?<name>\w+)\s*\([^)]*\)\s*(?::\s*\w+\s*)?\{");
            if (m.Success)
                return m.Groups["name"].Value;

            // A resource or a static global.
            m = System.Text.RegularExpressions.Regex.Match(text, @"(?<name>\w+)\s*(?::\s*register\([^)]*\))?\s*;\s*$");
            return m.Success ? m.Groups["name"].Value : null;
        }

        /// <summary>The named function, signature through matching brace.</summary>
        private static string ExtractFunction(string hlsl, string name, ContentIdentity identity)
        {
            var signature = new System.Text.RegularExpressions.Regex(
                @"^[^\r\n/#][^\r\n]*\b" + name + @"\s*\(",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            System.Text.RegularExpressions.Match m = signature.Match(hlsl);
            if (!m.Success)
                throw new InvalidContentException("Generated HLSL has no function '" + name + "'.", identity);

            int open = hlsl.IndexOf('{', m.Index);
            if (open < 0)
                throw new InvalidContentException("Function '" + name + "' has no body.", identity);

            int depth = 0;
            for (int i = open; i < hlsl.Length; i++)
            {
                if (hlsl[i] == '{')
                    depth++;
                else if (hlsl[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return hlsl.Substring(m.Index, i - m.Index + 1);
                }
            }

            throw new InvalidContentException("Function '" + name + "' is not closed.", identity);
        }

        // ---- generation ---------------------------------------------------

        private static string Generate(
            string slangc, string spirvCross, string source, string entryPoint, string stage,
            bool isGles, string temp, ContentIdentity identity,
            List<RawGlslAttributeInfo> attributes)
        {
            string spv = Path.Combine(temp, entryPoint + ".spv");
            Run(slangc, Quote(source) + " -target spirv -entry " + entryPoint + " -stage " + stage + " -o " + Quote(spv),
                identity, "slangc");

            var args = new StringBuilder();
            args.Append(Quote(spv));
            args.Append(isGles ? " --es --version 300" : " --no-es --version 330");

            // The flag this whole approach rests on: a std140 block becomes a
            // flat vec4 array, which is what the runtime uploads.
            args.Append(" --flatten-ubo");

            // Both stages renamed to one scheme so the varyings link. See this
            // class's header - GLSL 330 links them by name, and spirv-cross
            // names the two sides differently.
            string direction = stage == "vertex" ? "out" : "in";
            for (int i = 0; i < MaxVaryingLocations; i++)
                args.Append(" --rename-interface-variable " + direction + " " + i + " " + VaryingPrefix + i);

            // THE VERTEX STAGE ALSO RENAMES ITS INPUTS, and forgetting this is
            // silent: spirv-cross calls them "input_Position" and the runtime
            // looks each attribute up with glGetAttribLocation(<the name this
            // effect declares>). A miss is -1, the attribute simply never gets
            // bound, and every shape renders as nothing at all with no error
            // anywhere. Locations follow the vertex input struct's field
            // order, which is why the manifest's attribute order has to match
            // that struct - see the .slangfx's own note.
            if (stage == "vertex")
            {
                for (int i = 0; i < attributes.Count; i++)
                    args.Append(" --rename-interface-variable in " + i + " " + attributes[i].Name);
            }

            string glsl = Path.Combine(temp, entryPoint + ".glsl");
            args.Append(" --output " + Quote(glsl));

            Run(spirvCross, args.ToString(), identity, "spirv-cross");

            string text = File.ReadAllText(glsl);

            // The version line is the pipeline's to add, not the generator's:
            // RawGlslEffectProcessor prepends "#version 330 core" for desktop,
            // and the WebGL2 runtime prepends "#version 300 es" itself.
            text = StripVersion(text);

            // The runtime finds the constant buffer by the name
            // RawGlslEffectProcessor gives it.
            text = text.Replace(SlangUniformArrayName(text), RawGlslConstantBufferName);

            // HIGHP, NOT MEDIUMP. spirv-cross defaults GLSL ES fragment shaders
            // to "precision mediump float;" and offers no flag to change it,
            // and mediump is not enough for either of the things this pipeline
            // does:
            //
            //  - SDF TEXT. The glyph alpha is a smoothstep across a distance
            //    field, and at mediump the ramp quantises enough to EAT THIN
            //    STROKES -- "blind" rendered as "olinc" in a real browser,
            //    which is how this was found. The hand-written shaders this
            //    replaced all declared highp for exactly this reason.
            //  - CLIP RECTS, which are device pixels. mediump guarantees only
            //    ~10 bits of mantissa, so integers stop being exact past 2048
            //    -- and a maximised window on this machine is 3840 wide.
            //
            // ES 3.0 requires highp support in fragment shaders, so this is not
            // a gamble.
            if (isGles)
                text = text.Replace("precision mediump float;", "precision highp float;");

            return text;
        }

        private const string RawGlslConstantBufferName = "Params";

        /// <summary>
        /// Whatever spirv-cross called the flattened uniform array. Found by
        /// looking rather than assumed, because the name is derived from the
        /// Slang parameter group and would change with it.
        ///
        /// A PRECISION QUALIFIER SITS BETWEEN "uniform" AND "vec4", and missing
        /// it cost a long hunt. GLSL 330 emits "uniform vec4 X[6];" but GLSL ES
        /// 300 emits "uniform highp vec4 X[6];" in the FRAGMENT shader (the
        /// vertex shader has no qualifier, so it matched and looked fine).
        /// Matching the bare literal therefore renamed the array in the vertex
        /// stage and NOT in the fragment stage on WebGL2 -- the runtime looks
        /// the buffer up by the constant buffer's name, failed to find it, and
        /// never uploaded it, so every uniform the fragment read came back
        /// zero. For SDF text that means Smoothing == 0, which turns the
        /// antialiasing ramp smoothstep(0.5 - s, 0.5 + s, d) into a hard step
        /// at 0.5: glyphs came out aliased and eroded, on that one backend
        /// only. GeometryBatch was immune purely because its fragment shader
        /// reads no uniforms at all.
        /// </summary>
        private static string SlangUniformArrayName(string glsl)
        {
            var declaration = new System.Text.RegularExpressions.Regex(
                @"uniform\s+(?:lowp\s+|mediump\s+|highp\s+)?vec4\s+(?<name>\w+)\s*\[");

            System.Text.RegularExpressions.Match m = declaration.Match(glsl);
            return m.Success ? m.Groups["name"].Value : RawGlslConstantBufferName;
        }

        private static string StripVersion(string glsl)
        {
            if (!glsl.StartsWith("#version", StringComparison.Ordinal))
                return glsl;

            int newline = glsl.IndexOf('\n');
            return newline < 0 ? string.Empty : glsl.Substring(newline + 1);
        }

        /// <summary>
        /// KNI's GL-vs-D3D coordinate-convention correction, as the last two
        /// statements of main(). MojoShader appends this automatically after
        /// translation; this path never goes near MojoShader, so it is appended
        /// here - the same two lines every hand-written .vert in this pipeline
        /// ends with.
        /// </summary>
        private static string AppendPosFixup(string vertexGlsl, ContentIdentity identity)
        {
            int close = vertexGlsl.LastIndexOf('}');
            if (close < 0)
                throw new InvalidContentException("Generated vertex GLSL has no closing brace to append posFixup to.", identity);

            string body = vertexGlsl.Substring(0, close)
                + "    gl_Position.y = gl_Position.y * posFixup.y;\n"
                + "    gl_Position.xy += posFixup.zw * gl_Position.ww;\n}\n";

            int main = body.IndexOf("void main", StringComparison.Ordinal);
            if (main < 0)
                throw new InvalidContentException("Generated vertex GLSL has no main() to append posFixup to.", identity);

            return body.Substring(0, main) + "uniform vec4 posFixup;\n\n" + body.Substring(main);
        }

        /// <summary>
        /// The sampler uniforms the generated fragment shader declares.
        ///
        /// PRECISION QUALIFIERS ARE PART OF THE DECLARATION, and missing them
        /// was a real bug with a very confusing symptom. GLSL 330 emits
        /// "uniform sampler2D X;" but GLSL ES 300 emits "uniform highp
        /// sampler2D X;", so matching the literal "uniform sampler2D " found
        /// every sampler on desktop and NONE on WebGL2 -- the effect then
        /// shipped with an empty sampler list, nothing bound the sampler to a
        /// texture unit or applied the caller's sampler state, and the font
        /// atlas got sampled with default filtering. Glyph edges came out hard
        /// and eroded ON THE WEB ONLY, while shapes were unaffected because
        /// they sample a single white texel where filtering cannot show.
        /// </summary>
        private static List<string> FindSamplers(string glsl)
        {
            var found = new List<string>();
            var declaration = new System.Text.RegularExpressions.Regex(
                @"uniform\s+(?:lowp\s+|mediump\s+|highp\s+)?sampler(?:1D|2D|3D|Cube|2DArray)\s+(?<name>\w+)");

            foreach (System.Text.RegularExpressions.Match m in declaration.Matches(glsl))
                found.Add(m.Groups["name"].Value);

            return found;
        }

        // ---- reflection ---------------------------------------------------

        /// <summary>
        /// The constant buffer's fields and their byte offsets, straight from
        /// slangc. These are std140 offsets and they are NOT what a person
        /// guesses - a vec2 after a float lands at +8, not +4 - which is
        /// exactly why they are read rather than written down.
        /// </summary>
        private static List<RawGlslParameterInfo> ReflectParameters(
            string slangc, string source, string entryPoint, string temp, ContentIdentity identity)
        {
            string json = Path.Combine(temp, "reflection.json");
            string spv = Path.Combine(temp, "reflection.spv");
            Run(slangc, Quote(source) + " -target spirv -entry " + entryPoint + " -stage vertex -o " + Quote(spv)
                + " -reflection-json " + Quote(json), identity, "slangc (reflection)");

            SlangJsonValue root = SlangJson.Parse(File.ReadAllText(json));
            var parameters = new List<RawGlslParameterInfo>();

            SlangJsonValue all = root["parameters"];
            if (all == null || all.Array == null)
                return parameters;

            foreach (SlangJsonValue p in all.Array)
            {
                SlangJsonValue type = p["type"];
                if (type == null || type["kind"] == null || type["kind"].AsString != "constantBuffer")
                    continue;

                SlangJsonValue fields = type["elementType"] == null ? null : type["elementType"]["fields"];
                if (fields == null || fields.Array == null)
                    continue;

                foreach (SlangJsonValue f in fields.Array)
                {
                    SlangJsonValue ft = f["type"];
                    SlangJsonValue binding = f["binding"];
                    if (ft == null || binding == null)
                        continue;

                    string shape = ShapeOf(ft, f["name"] == null ? "?" : f["name"].AsString, identity);
                    parameters.Add(new RawGlslParameterInfo
                    {
                        Name = f["name"].AsString,
                        Type = shape,
                        Offset = binding["offset"].AsInt(0),
                    });
                }
            }

            return parameters;
        }

        private static string ShapeOf(SlangJsonValue type, string name, ContentIdentity identity)
        {
            string kind = type["kind"] == null ? "" : type["kind"].AsString;
            switch (kind)
            {
                case "scalar":
                    return "float";

                case "vector":
                    return "vec" + type["elementCount"].AsInt(4);

                case "matrix":
                    {
                        int rows = type["rowCount"].AsInt(4);
                        int columns = type["columnCount"].AsInt(4);
                        if (rows == 4 && columns == 4)
                            return "mat4";

                        throw new InvalidContentException(
                            string.Format("Uniform '{0}' is a {1}x{2} matrix; only 4x4 is supported.", name, rows, columns), identity);
                    }

                default:
                    throw new InvalidContentException(
                        string.Format("Uniform '{0}' has unsupported reflected kind '{1}'.", name, kind), identity);
            }
        }

        // ---- external tools -----------------------------------------------

        private static string FindTool(string envVar, string exeName, string sdkEnvVar, ContentIdentity identity, string how)
        {
            string explicitPath = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
                return explicitPath;

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0)
                    continue;

                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // A malformed PATH entry is not worth failing the build over.
                }
            }

            if (!string.IsNullOrEmpty(sdkEnvVar))
            {
                string sdk = Environment.GetEnvironmentVariable(sdkEnvVar);
                if (!string.IsNullOrEmpty(sdk))
                {
                    string candidate = Path.Combine(Path.Combine(sdk, "Bin"), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            throw new InvalidContentException(
                "Could not find " + exeName + ", which building a .slangfx needs. " + how, identity);
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static void Run(string exe, string arguments, ContentIdentity identity, string what)
        {
            var info = new System.Diagnostics.ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;

            // Fully qualified: this class's own Process(...) override shadows
            // the type name inside it.
            using (System.Diagnostics.Process process = new System.Diagnostics.Process())
            {
                process.StartInfo = info;

                // BOTH PIPES DRAINED CONCURRENTLY, which is not fussiness: the
                // obvious ReadToEnd() on stdout followed by ReadToEnd() on
                // stderr DEADLOCKS the moment the child fills the stderr pipe
                // while this end is still blocked on stdout. slangc warns on
                // every 'register' without a Vulkan binding, which is enough
                // output to hit it -- the first run of this processor hung
                // exactly there, with slangc sat waiting for a reader.
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidContentException(
                        what + " failed (exit " + process.ExitCode + "):" + Environment.NewLine
                        + arguments + Environment.NewLine + stderr + stdout, identity);
                }
            }
        }
    }
}
