// See https://github.com/manyeyes for more information
// Copyright (c)  2024 by manyeyes

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AliParaformerAsr.Model;
using AliParaformerAsr.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;
using System.Text.RegularExpressions;

// 模型文件地址： https://modelscope.cn/models/iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx
// 模型文件地址： https://www.modelscope.cn/models/manyeyes/sensevoice-small-onnx
namespace AliParaformerAsr
{
    /// <summary>
    /// offline recognizer package
    /// Copyright (c)  2023 by manyeyes
    /// </summary>
    public class OfflineRecognizer : IDisposable
    {
        private InferenceSession _onnxSession;
        private readonly ILogger<OfflineRecognizer> _logger;
        private WavFrontend _wavFrontend;
        private string _frontend;
        private FrontendConfEntity _frontendConfEntity;
        private string[] _tokens;
        private IOfflineProj? _offlineProj;
        private OfflineModel _offlineModel;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="modelFilePath"></param>
        /// <param name="configFilePath"></param>
        /// <param name="mvnFilePath"></param>
        /// <param name="tokensFilePath"></param>
        /// <param name="rumtimeType">可以选择gpu，但是目前情况下，不建议使用，因为性能提升有限</param>
        /// <param name="deviceId">设备id，多显卡时用于指定执行的显卡</param>
        /// <param name="batchSize"></param>
        /// <param name="threadsNum"></param>
        /// <exception cref="ArgumentException"></exception>
        public OfflineRecognizer(string modelFilePath, string configFilePath, string mvnFilePath, string tokensFilePath, int threadsNum = 1, OnnxRumtimeTypes rumtimeType = OnnxRumtimeTypes.CPU, int deviceId = 0)
        {
            _offlineModel = new OfflineModel(modelFilePath, threadsNum);
            
            string[] tokenLines;
            if (tokensFilePath.EndsWith(".txt"))
            {
                tokenLines = File.ReadAllLines(tokensFilePath);
            }
            else if (tokensFilePath.EndsWith(".json"))
            {
                using JsonDocument tokenDocument = JsonDocument.Parse(File.ReadAllText(tokensFilePath));
                if (tokenDocument.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("Invalid tokens file format. JSON tokens must be an array.");
                }

                tokenLines = tokenDocument.RootElement
                    .EnumerateArray()
                    .Select(token => token.GetString() ?? string.Empty)
                    .ToArray();
            }
            else
            {
                throw new ArgumentException("Invalid tokens file format. Only .txt and .json are supported.");
            }

            _tokens = tokenLines;

            OfflineYamlEntity offlineYamlEntity = YamlHelper.ReadYaml<OfflineYamlEntity>(configFilePath);
            switch (offlineYamlEntity.model.ToLower())
            {
                case "paraformer":
                case "contextualparaformer":
                    _offlineProj = new OfflineProjOfParaformer(_offlineModel);
                    break;
                case "sensevoicesmall":
                    _offlineProj = new OfflineProjOfSenseVoiceSmall(_offlineModel);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported offline model type: {offlineYamlEntity.model}");
            }
            _wavFrontend = new WavFrontend(mvnFilePath, offlineYamlEntity.frontend_conf);
            _frontend = offlineYamlEntity.frontend;
            _frontendConfEntity = offlineYamlEntity.frontend_conf;
            ILoggerFactory loggerFactory = new LoggerFactory();
            _logger = new Logger<OfflineRecognizer>(loggerFactory);
        }

        public List<string> GetResults(List<float[]> samples)
        {
            _logger.LogInformation("get features begin");
            List<OfflineInputEntity> offlineInputEntities = ExtractFeats(samples);
            OfflineOutputEntity modelOutput = Forward(offlineInputEntities);
            List<string> text_results = DecodeMulti(modelOutput.Token_nums ?? new List<int[]>());
            return text_results;
        }

        private List<OfflineInputEntity> ExtractFeats(List<float[]> waveform_list)
        {
            List<float[]> in_cache = new List<float[]>();
            List<OfflineInputEntity> offlineInputEntities = new List<OfflineInputEntity>();
            foreach (var waveform in waveform_list)
            {
                float[] fbanks = _wavFrontend.GetFbank(waveform);
                float[] features = _wavFrontend.LfrCmvn(fbanks);
                OfflineInputEntity offlineInputEntity = new OfflineInputEntity();
                offlineInputEntity.Speech = features;
                offlineInputEntity.SpeechLength = features.Length;
                offlineInputEntities.Add(offlineInputEntity);
            }
            return offlineInputEntities;
        }

        private OfflineOutputEntity Forward(List<OfflineInputEntity> modelInputs)
        {
            OfflineOutputEntity offlineOutputEntity = new OfflineOutputEntity();            
            if (_offlineProj is null)
            {
                throw new InvalidOperationException("Offline model projection is not initialized.");
            }

            ModelOutputEntity modelOutputEntity = _offlineProj.ModelProj(modelInputs);
            if (modelOutputEntity?.model_out is not null)
            {
                offlineOutputEntity.Token_nums_length = modelOutputEntity.model_out_lens ?? Array.Empty<int>();
                Tensor<float> logitsTensor = modelOutputEntity.model_out;
                var dimensions = logitsTensor.Dimensions;
                if (dimensions.Length != 3)
                {
                    throw new InvalidOperationException($"Unexpected ASR output rank: {dimensions.Length}.");
                }

                int batchSize = dimensions[0];
                int frameCount = dimensions[1];
                int vocabSize = dimensions[2];
                if (batchSize <= 0 || frameCount <= 0 || vocabSize <= 0)
                {
                    throw new InvalidOperationException(
                        $"Unexpected ASR output shape: [{string.Join(", ", dimensions.ToArray())}].");
                }

                float[] logits = logitsTensor.ToArray();
                List<int[]> token_nums = new List<int[]> { };

                for (int i = 0; i < batchSize; i++)
                {
                    int validFrames = frameCount;
                    if (i < offlineOutputEntity.Token_nums_length.Length)
                    {
                        validFrames = Math.Clamp(offlineOutputEntity.Token_nums_length[i], 0, frameCount);
                    }

                    int[] item = new int[validFrames];
                    for (int j = 0; j < validFrames; j++)
                    {
                        int token_num = 0;
                        int frameOffset = ((i * frameCount) + j) * vocabSize;
                        for (int k = 1; k < vocabSize; k++)
                        {
                            token_num = logits[frameOffset + token_num] > logits[frameOffset + k] ? token_num : k;
                        }
                        item[j] = (int)token_num;
                    }
                    token_nums.Add(item);
                }
                offlineOutputEntity.Token_nums = token_nums;
            }
            return offlineOutputEntity;
        }

        private List<string> DecodeMulti(List<int[]> token_nums)
        {
            List<string> text_results = new List<string>();
            foreach (int[] token_num in token_nums)
            {
                string text_result = "";
                foreach (int token in token_num)
                {
                    if (token == 2)
                    {
                        break;
                    }

                    if ((uint)token >= (uint)_tokens.Length)
                    {
                        continue;
                    }

                    string tokenChar = _tokens[token].Split("\t")[0];

                    if (tokenChar != "</s>" && tokenChar != "<s>" && tokenChar != "<blank>" && tokenChar != "<unk>")
                    {                        
                        if (IsChinese(tokenChar, true))
                        {
                            text_result += tokenChar;
                        }
                        else
                        {
                            text_result += "▁" + tokenChar + "▁";
                        }
                    }
                }
                text_results.Add(text_result.Replace("@@▁▁", "").Replace("▁▁", " ").Replace("▁", ""));
            }

            return text_results;
        }

        /// <summary>
        /// Verify if the string is in Chinese.
        /// </summary>
        /// <param name="checkedStr">The string to be verified.</param>
        /// <param name="allMatch">Is it an exact match. When the value is true,all are in Chinese; 
        /// When the value is false, only Chinese is included.
        /// </param>
        /// <returns></returns>
        private bool IsChinese(string checkedStr, bool allMatch)
        {
            string pattern;
            if (allMatch)
                pattern = @"^[\u4e00-\u9fa5]+$";
            else
                pattern = @"[\u4e00-\u9fa5]";
            if (Regex.IsMatch(checkedStr, pattern))
                return true;
            else
                return false;
        }

        public void Dispose()
        {
            _offlineProj?.Dispose();
            _offlineModel.Dispose();
            GC.SuppressFinalize(this);
        }
        
    }
}
