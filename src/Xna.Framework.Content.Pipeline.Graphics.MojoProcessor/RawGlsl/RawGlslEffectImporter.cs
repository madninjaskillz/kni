// Copyright (C)2026 - Raw GLSL effect pipeline extension.

using System;
using System.IO;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;

namespace Microsoft.Xna.Framework.Content.Pipeline
{
    /// <summary>
    /// Reads a ".glslfx" manifest: a small line-based format (not HLSL,
    /// not JSON - kept deliberately simple to parse without extra
    /// dependencies in a net40-targeting pipeline assembly) that points at
    /// one shared vertex-shader GLSL file, a fixed-layout parameter list,
    /// sampler bindings, and one pixel-shader GLSL file per named
    /// technique. See RawGlslEffectProcessor for the full manifest grammar
    /// and why this exists at all.
    /// </summary>
    [ContentImporter(".glslfx", DisplayName = "Raw GLSL Effect Importer - KNI", DefaultProcessor = "RawGlslEffectProcessor")]
    public class RawGlslEffectImporter : ContentImporter<RawGlslEffectContent>
    {
        public override RawGlslEffectContent Import(string filename, ContentImporterContext context)
        {
            RawGlslEffectContent content = new RawGlslEffectContent();
            content.Identity = new ContentIdentity(filename);
            content.Name = Path.GetFileNameWithoutExtension(filename);

            string dir = Path.GetDirectoryName(filename);
            string[] lines = File.ReadAllLines(filename);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int colon = line.IndexOf(':');
                if (colon < 0)
                    throw new InvalidContentException("Malformed line in .glslfx manifest (expected 'key: value'): " + line, content.Identity);

                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string[] parts = value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                switch (key)
                {
                    case "vertex":
                        {
                            string path = Path.Combine(dir, value);
                            content.VertexShaderSource = File.ReadAllText(path);
                            context.AddDependency(path);
                        }
                        break;

                    case "param":
                        {
                            if (parts.Length != 3)
                                throw new InvalidContentException("Malformed 'param' line (expected 'param: Name type offset'): " + line, content.Identity);

                            RawGlslParameterInfo p = new RawGlslParameterInfo();
                            p.Name = parts[0];
                            p.Type = parts[1];
                            p.Offset = int.Parse(parts[2]);
                            content.Parameters.Add(p);
                        }
                        break;

                    case "sampler":
                        {
                            if (parts.Length != 2)
                                throw new InvalidContentException("Malformed 'sampler' line (expected 'sampler: slot Name'): " + line, content.Identity);

                            RawGlslSamplerInfo s = new RawGlslSamplerInfo();
                            s.Slot = int.Parse(parts[0]);
                            s.Name = parts[1];
                            content.Samplers.Add(s);
                        }
                        break;

                    case "technique":
                        {
                            if (parts.Length != 2)
                                throw new InvalidContentException("Malformed 'technique' line (expected 'technique: Name file.frag'): " + line, content.Identity);

                            RawGlslTechniqueInfo t = new RawGlslTechniqueInfo();
                            t.Name = parts[0];
                            string path = Path.Combine(dir, parts[1]);
                            t.PixelShaderSource = File.ReadAllText(path);
                            context.AddDependency(path);
                            content.Techniques.Add(t);
                        }
                        break;

                    default:
                        throw new InvalidContentException("Unknown key '" + key + "' in .glslfx manifest.", content.Identity);
                }
            }

            if (string.IsNullOrEmpty(content.VertexShaderSource))
                throw new InvalidContentException("The .glslfx manifest must declare a 'vertex:' shader.", content.Identity);
            if (content.Techniques.Count == 0)
                throw new InvalidContentException("The .glslfx manifest must declare at least one 'technique:'.", content.Identity);

            return content;
        }
    }
}
