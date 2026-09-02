// Copyright (C)2026 - Slang effect pipeline extension.
//
// Reads a ".slangfx" manifest. Deliberately the same shape of tiny line-based
// format RawGlslEffectImporter uses, for the same reason (this assembly
// multi-targets net40 — no System.Text.Json here).

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    [ContentImporter(".slangfx", DisplayName = "Slang Effect Importer - KNI", DefaultProcessor = "SlangEffectProcessor")]
    public class SlangEffectImporter : ContentImporter<SlangEffectContent>
    {
        public override SlangEffectContent Import(string filename, ContentImporterContext context)
        {
            SlangEffectContent content = new SlangEffectContent();
            content.Identity = new ContentIdentity(filename);

            string dir = Path.GetDirectoryName(filename);

            foreach (string rawLine in File.ReadAllLines(filename))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int colon = line.IndexOf(':');
                if (colon < 0)
                    throw new InvalidContentException("Malformed line in .slangfx manifest (expected 'key: value'): " + line, content.Identity);

                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string[] parts = value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                switch (key)
                {
                    case "source":
                        {
                            content.SourcePath = Path.Combine(dir, value);
                            context.AddDependency(content.SourcePath);
                        }
                        break;

                    case "attribute":
                        {
                            if (parts.Length != 3)
                                throw new InvalidContentException("Malformed 'attribute' line (expected 'attribute: Name Usage index'): " + line, content.Identity);

                            RawGlslAttributeInfo a = new RawGlslAttributeInfo();
                            a.Name = parts[0];
                            a.Usage = parts[1];
                            a.Index = int.Parse(parts[2]);
                            content.Attributes.Add(a);
                        }
                        break;

                    case "profile":
                        {
                            if (parts.Length != 2)
                                throw new InvalidContentException(
                                    "Malformed 'profile' line (expected 'profile: vs_profile ps_profile'): " + line, content.Identity);

                            content.VertexProfile = parts[0];
                            content.PixelProfile = parts[1];
                        }
                        break;

                    case "sampler":
                        {
                            if (parts.Length != 2)
                                throw new InvalidContentException("Malformed 'sampler' line (expected 'sampler: slot Name'): " + line, content.Identity);

                            RawGlslSamplerInfo si = new RawGlslSamplerInfo();
                            si.Slot = int.Parse(parts[0]);
                            si.Name = parts[1];
                            content.Samplers.Add(si);
                        }
                        break;

                    case "technique":
                        {
                            if (parts.Length != 3)
                                throw new InvalidContentException(
                                    "Malformed 'technique' line (expected 'technique: Name vertexEntry fragmentEntry'): " + line, content.Identity);

                            SlangTechniqueInfo t = new SlangTechniqueInfo();
                            t.Name = parts[0];
                            t.VertexEntryPoint = parts[1];
                            t.FragmentEntryPoint = parts[2];
                            content.Techniques.Add(t);
                        }
                        break;

                    default:
                        throw new InvalidContentException("Unknown key '" + key + "' in .slangfx manifest.", content.Identity);
                }
            }

            if (string.IsNullOrEmpty(content.SourcePath))
                throw new InvalidContentException("The .slangfx manifest must declare a 'source:' .slang file.", content.Identity);
            if (content.Techniques.Count == 0)
                throw new InvalidContentException("The .slangfx manifest must declare at least one 'technique:'.", content.Identity);

            return content;
        }
    }
}
