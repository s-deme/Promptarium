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
            var result = new ParsedGeneration();
            var textNodes = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonElement? samplerNode = null;
            var unknownNodeCount = 0;

            foreach (var (nodeId, node) in nodes)
            {
                var classType = GetString(node, "class_type") ?? string.Empty;
                var inputs = GetInputs(node);
                if (classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GetInputString(inputs, "text");
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
                else if (classType.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase))
                {
                    result.ModelName ??= GetInputScalar(inputs, "ckpt_name");
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
                else if (!IsExpectedClass(classType))
                {
                    unknownNodeCount++;
                }
            }

            if (samplerNode is { } sampler)
            {
                var samplerInputs = GetInputs(sampler);
                result.PositivePrompt = ResolveTextInput(samplerInputs, "positive", textNodes);
                result.NegativePrompt = ResolveTextInput(samplerInputs, "negative", textNodes);
            }

            if (string.IsNullOrWhiteSpace(result.PositivePrompt) && textNodes.Count > 0)
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
            var textNodes = new Dictionary<int, string>();
            var result = new ParsedGeneration { ValueSource = "ComfyUI workflow JSON（UIワークフロー由来）" };
            JsonElement? sampler = null;
            var unknownNodeCount = 0;

            foreach (var (nodeId, node) in nodes)
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
                else if (type.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase))
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
                else if (!IsExpectedClass(type))
                {
                    unknownNodeCount++;
                }
            }

            if (sampler is { } samplerNode)
            {
                result.PositivePrompt = ResolveWorkflowTextInput(samplerNode, "positive", linkOrigins, textNodes);
                result.NegativePrompt = ResolveWorkflowTextInput(samplerNode, "negative", linkOrigins, textNodes);
            }

            if (string.IsNullOrWhiteSpace(result.PositivePrompt) && textNodes.Count > 0)
            {
                result.PositivePrompt = textNodes.Values.First();
                result.NegativePrompt ??= textNodes.Values.Skip(1).FirstOrDefault();
            }

            var hasCoreValues = !string.IsNullOrWhiteSpace(result.PositivePrompt) || !string.IsNullOrWhiteSpace(result.ModelName) || !string.IsNullOrWhiteSpace(result.Seed);
            result.Status = hasCoreValues ? unknownNodeCount > 0 ? ParseStatus.UnknownNodes : ParseStatus.Partial : ParseStatus.Partial;
            result.Message = hasCoreValues
                ? "workflow JSONから標準UI構成の値を暫定的に導出しました。"
                : "workflow JSONは保持しましたが、標準UI構成として導出できる値がありません。";
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
        classType.Contains("VAEDecode", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("CLIPLoader", StringComparison.OrdinalIgnoreCase) ||
        classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase);

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

    private static string? ResolveTextInput(JsonElement inputs, string inputName, IReadOnlyDictionary<string, string> textNodes)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(inputName, out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = input.EnumerateArray().ToArray();
        return values.Length > 0 && values[0].ValueKind == JsonValueKind.String && textNodes.TryGetValue(values[0].GetString()!, out var text)
            ? text
            : null;
    }

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
            if (values.Length >= 2 && values[0].TryGetInt32(out var linkId) && values[1].TryGetInt32(out var originId)) links[linkId] = originId;
        }
        return links;
    }

    private static string? ResolveWorkflowTextInput(JsonElement node, string inputName, IReadOnlyDictionary<int, int> linkOrigins, IReadOnlyDictionary<int, string> textNodes)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) return null;
        foreach (var input in inputs.EnumerateArray())
        {
            if (!string.Equals(GetString(input, "name"), inputName, StringComparison.OrdinalIgnoreCase) || !input.TryGetProperty("link", out var link) || !link.TryGetInt32(out var linkId)) continue;
            return linkOrigins.TryGetValue(linkId, out var nodeId) && textNodes.TryGetValue(nodeId, out var text) ? text : null;
        }
        return null;
    }
}
