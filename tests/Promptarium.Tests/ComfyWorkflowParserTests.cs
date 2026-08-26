using Promptarium.Models;
using Promptarium.Services;
using Xunit;

namespace Promptarium.Tests;

public sealed class ComfyWorkflowParserTests
{
    [Fact]
    public void PromptJson_Extracts_standard_values_dimensions_and_per_field_sources()
    {
        var metadata = new PngMetadata { IsPng = true, Width = 1024, Height = 768 };
        metadata.Text["prompt"] = """
            {
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"positive text"}},
              "2":{"class_type":"CLIPTextEncode","inputs":{"text":"negative text"}},
              "3":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"model.safetensors"}},
              "4":{"class_type":"LoraLoader","inputs":{"lora_name":"style.safetensors","strength_model":0.8,"strength_clip":0.7}},
              "5":{"class_type":"VAELoader","inputs":{"vae_name":"vae.safetensors"}},
              "6":{"class_type":"EmptyLatentImage","inputs":{"width":832,"height":1216}},
              "7":{"class_type":"KSampler","inputs":{"seed":123,"steps":28,"cfg":7.5,"sampler_name":"euler","scheduler":"normal","positive":["1",0],"negative":["2",0]}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal("positive text", result.PositivePrompt);
        Assert.Equal("negative text", result.NegativePrompt);
        Assert.Equal("model.safetensors", result.ModelName);
        Assert.Equal("vae.safetensors", result.VaeName);
        Assert.Equal("832", result.WorkflowWidth);
        Assert.Equal("1216", result.WorkflowHeight);
        Assert.Single(result.Loras);
        Assert.All(result.ValueSources.Values, value => Assert.Equal("ComfyUI prompt JSON", value));
    }

    [Fact]
    public void Custom_node_is_preserved_as_an_informative_unknown_state()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["prompt"] = """
            {
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"still usable"}},
              "2":{"class_type":"KSampler","inputs":{"seed":4,"positive":["1",0]}},
              "3":{"class_type":"StudioCustomNode","inputs":{}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal(ParseStatus.UnknownNodes, result.Status);
        Assert.Equal("still usable", result.PositivePrompt);
        Assert.Contains("1 件", result.Message);
    }
}
