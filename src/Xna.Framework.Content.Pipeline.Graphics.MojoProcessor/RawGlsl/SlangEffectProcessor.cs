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
            bool isGles;
            switch (context.TargetPlatform)
            {
                case TargetPlatform.DesktopGL: isGles = false; break;
                case TargetPlatform.BlazorGL: isGles = true; break;
                default:
                    throw new InvalidContentException(
                        string.Format("SlangEffectProcessor only supports DesktopGL/BlazorGL targets, not {0}.", context.TargetPlatform),
                        input.Identity);
            }

            string slangc = FindTool("SLANGC", "slangc.exe", null, input.Identity,
                "Slang (https://github.com/shader-slang/slang/releases) - set SLANGC to slangc.exe, or put it on PATH.");
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

                    foreach (string sampler in FindSamplers(ps))
                    {
                        if (!generated.Samplers.Exists(s => s.Name == sampler))
                        {
                            generated.Samplers.Add(new RawGlslSamplerInfo
                            {
                                // Unit by declaration order, matching the order
                                // the caller binds GraphicsDevice.Textures[n].
                                Slot = generated.Samplers.Count,
                                Name = sampler,
                            });
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

            return text;
        }

        private const string RawGlslConstantBufferName = "Params";

        /// <summary>
        /// Whatever spirv-cross called the flattened uniform array. Found by
        /// looking rather than assumed, because the name is derived from the
        /// Slang parameter group and would change with it.
        /// </summary>
        private static string SlangUniformArrayName(string glsl)
        {
            const string marker = "uniform vec4 ";
            int at = glsl.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return RawGlslConstantBufferName;

            int start = at + marker.Length;
            int end = glsl.IndexOf('[', start);
            if (end < 0)
                return RawGlslConstantBufferName;

            return glsl.Substring(start, end - start).Trim();
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

        private static List<string> FindSamplers(string glsl)
        {
            var found = new List<string>();
            const string marker = "uniform sampler2D ";
            int at = 0;
            while ((at = glsl.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + marker.Length;
                int end = glsl.IndexOfAny(new char[] { ';', '[', ' ' }, start);
                if (end > start)
                    found.Add(glsl.Substring(start, end - start).Trim());
                at = start;
            }

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
