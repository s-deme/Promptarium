using System.Text.Json;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class ComfyWorkflowParser
{
    public const string Version = "0.1";

    public ParsedGeneration Parse(PngMetadata metadata)
    {
        var hasPrompt = metadata.Text.TryGetValue("prompt", out var promptJson);
        var hasWorkflow = metadata.Text.TryGetValue("workflow", out var workflowJson);
        if (!hasPrompt && !hasWorkflow)
        {
            return new ParsedGeneration
            {
                Status = ParseStatus.NoMetadata,
                Message = "ComfyUIのprompt/workflowメタデータが見つかりません。"
            };
        }

        if (string.IsNullOrWhiteSpace(promptJson))
        {
            return ParseWorkflowOnly(workflowJson);
        }

        try
        {
            using var document = JsonDocument.Parse(promptJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ParsedGeneration { Status = ParseStatus.Corrupt, Message = "prompt JSONのルートがオブジェクトではありません。" };
            }

            var nodes = document.RootElement.EnumerateObject()
                .ToDictionary(node => node.Name, node => node.Value, StringComparer.Ordinal);
            var outputPathNodes = FindPromptOutputPathNodes(nodes);
            if (outputPathNodes.Count == 0)
            {
                return CreateNoOutputPathResult("ComfyUI prompt JSON");
            }
            var result = new ParsedGeneration();
            var textNodes = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonElement? samplerNode = null;
            var unknownNodeCount = 0;

            foreach (var (nodeId, node) in nodes.Where(pair => outputPathNodes.Contains(pair.Key)))
            {
                var classType = GetString(node, "class_type") ?? string.Empty;
                var inputs = GetInputs(node);
                if (classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GetPromptText(inputs);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textNodes[nodeId] = text;
                    }
                }
                else if (classType.Contains("KSampler", StringComparison.OrdinalIgnoreCase))
                {
                    samplerNode ??= node;
                    result.Seed ??= GetInputScalar(inputs, "seed");
                    result.Steps ??= GetInputScalar(inputs, "steps");
                    result.Cfg ??= GetInputScalar(inputs, "cfg");
                    result.Sampler ??= GetInputScalar(inputs, "sampler_name");
                    result.Scheduler ??= GetInputScalar(inputs, "scheduler");
                }
                else if (classType.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase) ||
                         classType.Contains("UNETLoader", StringComparison.OrdinalIgnoreCase) ||
                         classType.Contains("ModelLoader", StringComparison.OrdinalIgnoreCase))
                {
                    result.ModelName ??= GetInputScalar(inputs, "ckpt_name") ?? GetInputScalar(inputs, "model_name") ?? GetInputScalar(inputs, "unet_name");
                }
                else if (classType.Contains("Lora", StringComparison.OrdinalIgnoreCase))
                {
                    var name = GetInputScalar(inputs, "lora_name") ?? GetInputScalar(inputs, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Loras.Add(new LoraUsage(
                            name,
                            GetInputScalar(inputs, "strength_model"),
                            GetInputScalar(inputs, "strength_clip"),
                            result.Loras.Count));
                    }
                }
                else if (classType.Contains("VAE", StringComparison.OrdinalIgnoreCase) && classType.Contains("Loader", StringComparison.OrdinalIgnoreCase))
                {
                    result.VaeName ??= GetInputScalar(inputs, "vae_name");
                }
                else if (classType.Contains("Empty", StringComparison.OrdinalIgnoreCase) && classType.Contains("Latent", StringComparison.OrdinalIgnoreCase))
                {
                    result.WorkflowWidth ??= GetInputScalar(inputs, "width");
                    result.WorkflowHeight ??= GetInputScalar(inputs, "height");
                }
                else if (!IsExpectedClass(classType))
                {
                    unknownNodeCount++;
                }
            }

            if (samplerNode is { } sampler)
            {
                var samplerInputs = GetInputs(sampler);
                result.PositivePrompt = ResolvePromptConditioningText(samplerInputs, "positive", nodes);
                result.NegativePrompt = ResolvePromptConditioningText(samplerInputs, "negative", nodes);
            }

            if (samplerNode is null && string.IsNullOrWhiteSpace(result.PositivePrompt) && textNodes.Count > 0)
            {
                result.PositivePrompt = textNodes.Values.First();
                result.NegativePrompt ??= textNodes.Values.Skip(1).FirstOrDefault();
            }

            var hasCoreValues = !string.IsNullOrWhiteSpace(result.PositivePrompt) ||
                                !string.IsNullOrWhiteSpace(result.ModelName) ||
                                !string.IsNullOrWhiteSpace(result.Seed);
            result.Status = hasCoreValues
                ? unknownNodeCount > 0 ? ParseStatus.UnknownNodes : ParseStatus.Parsed
                : ParseStatus.Partial;
            result.Message = result.Status switch
            {
                ParseStatus.UnknownNodes => $"標準解析で取得できないノードが {unknownNodeCount} 件あります。",
                ParseStatus.Partial => "prompt JSONから一部の生成情報のみ取得できました。",
                _ => "標準ComfyUI経路から生成情報を取得しました。"
            };
            result.ValueSource = "ComfyUI prompt JSON";
            SetSourcesForParsedValues(result, result.ValueSource);
            return result;
        }
        catch (JsonException exception)
        {
            return new ParsedGeneration { Status = ParseStatus.Corrupt, Message = $"prompt JSONを解析できません: {exception.Message}" };
        }
        catch (Exception exception)
        {
            return new ParsedGeneration { Status = ParseStatus.Failed, Message = $"ComfyUI解析中にエラーが発生しました: {exception.Message}" };
        }
    }

    private static ParsedGeneration ParseWorkflowOnly(string? workflowJson)
    {
        if (string.IsNullOrWhiteSpace(workflowJson))
        {
            return new ParsedGeneration { Status = ParseStatus.NoMetadata, Message = "ComfyUIメタデータが見つかりません。" };
        }

        try
        {
            using var document = JsonDocument.Parse(workflowJson);
            if (!document.RootElement.TryGetProperty("nodes", out var nodeArray) || nodeArray.ValueKind != JsonValueKind.Array)
            {
                return new ParsedGeneration { Status = ParseStatus.Corrupt, Message = "workflow JSONにnodes配列がありません。", ValueSource = "ComfyUI workflow JSON" };
            }

            var nodes = nodeArray.EnumerateArray()
                .Where(node => GetNodeId(node) is not null)
                .ToDictionary(node => GetNodeId(node)!.Value, node => node);
            var linkOrigins = ReadLinkOrigins(document.RootElement);
            var outputPathNodes = FindWorkflowOutputPathNodes(nodes, linkOrigins);
            if (outputPathNodes.Count == 0)
            {
                return CreateNoOutputPathResult("ComfyUI workflow JSON（UIワークフロー由来）");
            }
            var textNodes = new Dictionary<int, string>();
            var result = new ParsedGeneration { ValueSource = "ComfyUI workflow JSON（UIワークフロー由来）" };
            JsonElement? sampler = null;
            var unknownNodeCount = 0;

            foreach (var (nodeId, node) in nodes.Where(pair => outputPathNodes.Contains(pair.Key)))
            {
                var type = GetString(node, "type") ?? string.Empty;
                if (type.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GetWidgetScalar(node, 0);
                    if (!string.IsNullOrWhiteSpace(text)) textNodes[nodeId] = text;
                }
                else if (type.Contains("KSampler", StringComparison.OrdinalIgnoreCase))
                {
                    sampler ??= node;
                    result.Seed ??= GetWidgetScalar(node, 0);
                    result.Steps ??= GetWidgetScalar(node, 2);
                    result.Cfg ??= GetWidgetScalar(node, 3);
                    result.Sampler ??= GetWidgetScalar(node, 4);
                    result.Scheduler ??= GetWidgetScalar(node, 5);
                }
                else if (type.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase) ||
                         type.Contains("UNETLoader", StringComparison.OrdinalIgnoreCase) ||
                         type.Contains("ModelLoader", StringComparison.OrdinalIgnoreCase))
                {
                    result.ModelName ??= GetWidgetScalar(node, 0);
                }
                else if (type.Contains("Lora", StringComparison.OrdinalIgnoreCase))
                {
                    var name = GetWidgetScalar(node, 0);
                    if (!string.IsNullOrWhiteSpace(name)) result.Loras.Add(new LoraUsage(name, GetWidgetScalar(node, 1), GetWidgetScalar(node, 2), result.Loras.Count));
                }
                else if (type.Contains("VAE", StringComparison.OrdinalIgnoreCase) && type.Contains("Loader", StringComparison.OrdinalIgnoreCase))
                {
                    result.VaeName ??= GetWidgetScalar(node, 0);
                }
                else if (type.Contains("Empty", StringComparison.OrdinalIgnoreCase) && type.Contains("Latent", StringComparison.OrdinalIgnoreCase))
                {
                    result.WorkflowWidth ??= GetWidgetScalar(node, 0);
                    result.WorkflowHeight ??= GetWidgetScalar(node, 1);
                }
                else if (!IsExpectedClass(type))
                {
                    unknownNodeCount++;
                }
            }

            if (sampler is { } samplerNode)
            {
                result.PositivePrompt = ResolveWorkflowConditioningText(samplerNode, "positive", linkOrigins, nodes);
                result.NegativePrompt = ResolveWorkflowConditioningText(samplerNode, "negative", linkOrigins, nodes);
            }

            if (sampler is null && string.IsNullOrWhiteSpace(result.PositivePrompt) && textNodes.Count > 0)
            {
                result.PositivePrompt = textNodes.Values.First();
                result.NegativePrompt ??= textNodes.Values.Skip(1).FirstOrDefault();
            }

            var hasCoreValues = !string.IsNullOrWhiteSpace(result.PositivePrompt) || !string.IsNullOrWhiteSpace(result.ModelName) || !string.IsNullOrWhiteSpace(result.Seed);
            result.Status = hasCoreValues ? unknownNodeCount > 0 ? ParseStatus.UnknownNodes : ParseStatus.Partial : ParseStatus.Partial;
            result.Message = hasCoreValues
                ? "workflow JSONから標準UI構成の値を暫定的に導出しました。"
                : "workflow JSONは保持しましたが、標準UI構成として導出できる値がありません。";
            SetSourcesForParsedValues(result, result.ValueSource);
            return result;
        }
        catch (JsonException exception)
        {
            return new ParsedGeneration { Status = ParseStatus.Corrupt, Message = $"workflow JSONを解析できません: {exception.Message}", ValueSource = "ComfyUI workflow JSON" };
        }
        catch (Exception exception)
        {
            return new ParsedGeneration { Status = ParseStatus.Failed, Message = $"workflow解析中にエラーが発生しました: {exception.Message}", ValueSource = "ComfyUI workflow JSON" };
        }
    }

    private static bool IsExpectedClass(string classType) =>
        classType.Contains("SaveImage", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("PreviewImage", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("VAEDecode", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("VAEEncode", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("LoadLatent", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("CLIPLoader", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("DualCLIPLoader", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("UNETLoader", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("Reroute", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("ImageScale", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("LatentUpscale", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("UpscaleModel", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("CLIPVision", StringComparison.OrdinalIgnoreCase) ||
        classType.StartsWith("Conditioning", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("ModelSampling", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("KSamplerSelect", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("RandomNoise", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("BasicScheduler", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("SamplerCustom", StringComparison.OrdinalIgnoreCase);

    private static ParsedGeneration CreateNoOutputPathResult(string source) => new()
    {
        Status = ParseStatus.Partial,
        ValueSource = source,
        Message = "SaveImage／PreviewImage などの画像出力ノードへ接続された経路を確認できないため、未接続ノードの値は抽出しません。"
    };

    private static HashSet<string> FindPromptOutputPathNodes(IReadOnlyDictionary<string, JsonElement> nodes)
    {
        var outputNodes = nodes
            .Where(pair => IsImageOutputNode(GetString(pair.Value, "class_type")))
            .Select(pair => pair.Key);
        var connected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(outputNodes);
        while (pending.TryPop(out var nodeId))
        {
            if (!nodes.TryGetValue(nodeId, out var node) || !connected.Add(nodeId)) continue;
            foreach (var upstreamNodeId in GetPromptInputLinks(GetInputs(node)))
            {
                if (nodes.ContainsKey(upstreamNodeId)) pending.Push(upstreamNodeId);
            }
        }

        return connected;
    }

    private static HashSet<int> FindWorkflowOutputPathNodes(IReadOnlyDictionary<int, JsonElement> nodes, IReadOnlyDictionary<int, int> linkOrigins)
    {
        var outputNodes = nodes
            .Where(pair => IsImageOutputNode(GetString(pair.Value, "type")))
            .Select(pair => pair.Key);
        var connected = new HashSet<int>();
        var pending = new Stack<int>(outputNodes);
        while (pending.TryPop(out var nodeId))
        {
            if (!nodes.TryGetValue(nodeId, out var node) || !connected.Add(nodeId)) continue;
            foreach (var upstreamNodeId in GetWorkflowInputLinks(node, linkOrigins))
            {
                if (nodes.ContainsKey(upstreamNodeId)) pending.Push(upstreamNodeId);
            }
        }

        return connected;
    }

    private static bool IsImageOutputNode(string? classType) =>
        !string.IsNullOrWhiteSpace(classType) &&
        (classType.Contains("SaveImage", StringComparison.OrdinalIgnoreCase) ||
         classType.Contains("PreviewImage", StringComparison.OrdinalIgnoreCase) ||
         classType.Contains("SaveAnimated", StringComparison.OrdinalIgnoreCase) ||
         classType.Contains("SaveVideo", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> GetPromptInputLinks(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) yield break;
        foreach (var input in inputs.EnumerateObject())
        {
            if (input.Value.ValueKind != JsonValueKind.Array) continue;
            var values = input.Value.EnumerateArray().ToArray();
            if (values.Length >= 2 && values[0].ValueKind == JsonValueKind.String && values[1].ValueKind == JsonValueKind.Number)
            {
                var nodeId = values[0].GetString();
                if (!string.IsNullOrWhiteSpace(nodeId)) yield return nodeId;
            }
        }
    }

    private static IEnumerable<int> GetWorkflowInputLinks(JsonElement node, IReadOnlyDictionary<int, int> linkOrigins)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var input in inputs.EnumerateArray())
        {
            if (input.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Number && link.TryGetInt32(out var linkId) && linkOrigins.TryGetValue(linkId, out var originId))
            {
                yield return originId;
            }
        }
    }

    private static void SetSourcesForParsedValues(ParsedGeneration result, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        if (!string.IsNullOrWhiteSpace(result.PositivePrompt)) result.SetSource("positive_prompt", source);
        if (!string.IsNullOrWhiteSpace(result.NegativePrompt)) result.SetSource("negative_prompt", source);
        if (!string.IsNullOrWhiteSpace(result.ModelName)) result.SetSource("model_name", source);
        if (!string.IsNullOrWhiteSpace(result.VaeName)) result.SetSource("vae_name", source);
        if (!string.IsNullOrWhiteSpace(result.Seed)) result.SetSource("seed", source);
        if (!string.IsNullOrWhiteSpace(result.Steps)) result.SetSource("steps", source);
        if (!string.IsNullOrWhiteSpace(result.Cfg)) result.SetSource("cfg", source);
        if (!string.IsNullOrWhiteSpace(result.Sampler)) result.SetSource("sampler", source);
        if (!string.IsNullOrWhiteSpace(result.Scheduler)) result.SetSource("scheduler", source);
        if (!string.IsNullOrWhiteSpace(result.WorkflowWidth)) result.SetSource("workflow_width", source);
        if (!string.IsNullOrWhiteSpace(result.WorkflowHeight)) result.SetSource("workflow_height", source);
        if (result.Loras.Count > 0) result.SetSource("loras", source);
    }

    private static JsonElement GetInputs(JsonElement node) =>
        node.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object ? inputs : default;

    private static string? GetString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetInputString(JsonElement inputs, string property) =>
        inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetInputScalar(JsonElement inputs, string property)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetPromptText(JsonElement inputs) =>
        GetInputString(inputs, "text")
        ?? GetInputString(inputs, "t5xxl")
        ?? GetInputString(inputs, "clip_l")
        ?? GetInputString(inputs, "text_g")
        ?? GetInputString(inputs, "text_l");

    private static string? ResolvePromptConditioningText(JsonElement inputs, string inputName, IReadOnlyDictionary<string, JsonElement> nodes)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(inputName, out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = input.EnumerateArray().ToArray();
        if (values.Length < 2 || values[0].ValueKind != JsonValueKind.String || values[1].ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var textParts = new List<string>();
        CollectPromptConditioningTexts(values[0].GetString(), nodes, new HashSet<string>(StringComparer.Ordinal), textParts);
        return JoinTextParts(textParts);
    }

    private static void CollectPromptConditioningTexts(string? nodeId, IReadOnlyDictionary<string, JsonElement> nodes, ISet<string> visited, ICollection<string> textParts)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || !visited.Add(nodeId) || !nodes.TryGetValue(nodeId, out var node)) return;
        var classType = GetString(node, "class_type") ?? string.Empty;
        if (classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetPromptText(GetInputs(node));
            if (!string.IsNullOrWhiteSpace(text)) textParts.Add(text);
            return;
        }

        foreach (var upstreamNodeId in GetPromptInputLinks(GetInputs(node)))
        {
            CollectPromptConditioningTexts(upstreamNodeId, nodes, visited, textParts);
        }
    }

    private static string? JoinTextParts(IReadOnlyCollection<string> textParts) =>
        textParts.Count == 0 ? null : string.Join(", ", textParts);

    private static int? GetNodeId(JsonElement node) =>
        node.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) ? id : null;

    private static string? GetWidgetScalar(JsonElement node, int index)
    {
        if (!node.TryGetProperty("widgets_values", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = widgets.EnumerateArray().ToArray();
        return index < values.Length ? GetScalar(values[index]) : null;
    }

    private static string? GetScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static Dictionary<int, int> ReadLinkOrigins(JsonElement root)
    {
        var links = new Dictionary<int, int>();
        if (!root.TryGetProperty("links", out var linkArray) || linkArray.ValueKind != JsonValueKind.Array) return links;
        foreach (var link in linkArray.EnumerateArray())
        {
            var values = link.ValueKind == JsonValueKind.Array ? link.EnumerateArray().ToArray() : [];
            if (values.Length >= 2 && values[0].ValueKind == JsonValueKind.Number && values[1].ValueKind == JsonValueKind.Number && values[0].TryGetInt32(out var linkId) && values[1].TryGetInt32(out var originId)) links[linkId] = originId;
        }
        return links;
    }

    private static string? ResolveWorkflowConditioningText(JsonElement node, string inputName, IReadOnlyDictionary<int, int> linkOrigins, IReadOnlyDictionary<int, JsonElement> nodes)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) return null;
        foreach (var input in inputs.EnumerateArray())
        {
            if (!string.Equals(GetString(input, "name"), inputName, StringComparison.OrdinalIgnoreCase) || !input.TryGetProperty("link", out var link) || link.ValueKind != JsonValueKind.Number || !link.TryGetInt32(out var linkId)) continue;
            if (!linkOrigins.TryGetValue(linkId, out var nodeId)) return null;
            var textParts = new List<string>();
            CollectWorkflowConditioningTexts(nodeId, nodes, linkOrigins, new HashSet<int>(), textParts);
            return JoinTextParts(textParts);
        }
        return null;
    }

    private static void CollectWorkflowConditioningTexts(int nodeId, IReadOnlyDictionary<int, JsonElement> nodes, IReadOnlyDictionary<int, int> linkOrigins, ISet<int> visited, ICollection<string> textParts)
    {
        if (!visited.Add(nodeId) || !nodes.TryGetValue(nodeId, out var node)) return;
        var type = GetString(node, "type") ?? string.Empty;
        if (type.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetWidgetScalar(node, 0);
            if (!string.IsNullOrWhiteSpace(text)) textParts.Add(text);
            return;
        }

        foreach (var upstreamNodeId in GetWorkflowInputLinks(node, linkOrigins))
        {
            CollectWorkflowConditioningTexts(upstreamNodeId, nodes, linkOrigins, visited, textParts);
        }
    }
}
