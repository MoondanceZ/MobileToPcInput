// See https://github.com/manyeyes for more information
// Copyright (c)  2023 by manyeyes
using System.Globalization;
using AliParaformerAsr.Model;

namespace AliParaformerAsr.Utils
{
    /// <summary>
    /// YamlHelper
    /// Copyright (c)  2023 by manyeyes
    /// </summary>
    internal class YamlHelper
    {
        public static T ReadYaml<T>(string yamlFilePath) where T : new()
        {
            if (!File.Exists(yamlFilePath))
            {
                return new T();
            }

            if (typeof(T) == typeof(OfflineYamlEntity))
            {
                return (T)(object)ReadOfflineYaml(yamlFilePath);
            }

            throw new NotSupportedException($"YAML type '{typeof(T).Name}' is not supported.");
        }

        private static OfflineYamlEntity ReadOfflineYaml(string yamlFilePath)
        {
            var entity = new OfflineYamlEntity();
            string currentSection = string.Empty;

            foreach (string rawLine in File.ReadLines(yamlFilePath))
            {
                string line = StripComment(rawLine);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                int indent = line.Length - line.TrimStart().Length;
                string trimmed = line.Trim();
                int separatorIndex = trimmed.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = trimmed[..separatorIndex].Trim();
                string value = Unquote(trimmed[(separatorIndex + 1)..].Trim());

                if (indent == 0)
                {
                    currentSection = string.IsNullOrEmpty(value) ? key : string.Empty;
                    ApplyRootValue(entity, key, value);
                    continue;
                }

                if (currentSection == "frontend_conf")
                {
                    ApplyFrontendConfValue(entity.frontend_conf, key, value);
                }
            }

            return entity;
        }

        private static void ApplyRootValue(OfflineYamlEntity entity, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            switch (key)
            {
                case "model":
                    entity.model = value;
                    break;
                case "frontend":
                    entity.frontend = value;
                    break;
                case "input_size":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inputSize))
                    {
                        entity.input_size = inputSize;
                    }
                    break;
                case "version":
                    entity.version = value;
                    break;
            }
        }

        private static void ApplyFrontendConfValue(FrontendConfEntity frontendConf, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            switch (key)
            {
                case "fs":
                    frontendConf.fs = ParseInt(value, frontendConf.fs);
                    break;
                case "window":
                    frontendConf.window = value;
                    break;
                case "n_mels":
                    frontendConf.n_mels = ParseInt(value, frontendConf.n_mels);
                    break;
                case "frame_length":
                    frontendConf.frame_length = ParseInt(value, frontendConf.frame_length);
                    break;
                case "frame_shift":
                    frontendConf.frame_shift = ParseInt(value, frontendConf.frame_shift);
                    break;
                case "dither":
                    frontendConf.dither = ParseFloat(value, frontendConf.dither);
                    break;
                case "lfr_m":
                    frontendConf.lfr_m = ParseInt(value, frontendConf.lfr_m);
                    break;
                case "lfr_n":
                    frontendConf.lfr_n = ParseInt(value, frontendConf.lfr_n);
                    break;
                case "snip_edges":
                    frontendConf.snip_edges = ParseBool(value, frontendConf.snip_edges);
                    break;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result
                : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out bool result) ? result : fallback;
        }

        private static string StripComment(string line)
        {
            int commentIndex = line.IndexOf('#');
            return commentIndex >= 0 ? line[..commentIndex] : line;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                return value[1..^1];
            }

            return value;
        }
    }
}
