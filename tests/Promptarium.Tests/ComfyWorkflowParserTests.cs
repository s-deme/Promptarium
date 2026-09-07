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
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"positive text","clip":["4",1]}},
              "2":{"class_type":"CLIPTextEncode","inputs":{"text":"negative text","clip":["4",1]}},
              "3":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"model.safetensors"}},
              "4":{"class_type":"LoraLoader","inputs":{"lora_name":"style.safetensors","strength_model":0.8,"strength_clip":0.7,"model":["3",0],"clip":["3",1]}},
              "5":{"class_type":"VAELoader","inputs":{"vae_name":"vae.safetensors"}},
              "6":{"class_type":"EmptyLatentImage","inputs":{"width":832,"height":1216}},
              "7":{"class_type":"KSampler","inputs":{"seed":123,"steps":28,"cfg":7.5,"sampler_name":"euler","scheduler":"normal","model":["4",0],"positive":["1",0],"negative":["2",0],"latent_image":["6",0]}},
              "8":{"class_type":"VAEDecode","inputs":{"samples":["7",0],"vae":["5",0]}},
              "9":{"class_type":"SaveImage","inputs":{"images":["8",0]}}
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
              "2":{"class_type":"KSampler","inputs":{"seed":4,"positive":["1",0],"latent_image":["3",0]}},
              "3":{"class_type":"StudioCustomNode","inputs":{}},
              "4":{"class_type":"SaveImage","inputs":{"images":["2",0]}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal(ParseStatus.UnknownNodes, result.Status);
        Assert.Equal("still usable", result.PositivePrompt);
        Assert.Contains("1 件", result.Message);
    }

    [Fact]
    public void PromptJson_ignores_unlinked_character_nodes_and_loras()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["prompt"] = """
            {
              "1":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"active-model.safetensors"}},
              "2":{"class_type":"LoraLoader","inputs":{"lora_name":"active-lora.safetensors","model":["1",0],"clip":["1",1]}},
              "3":{"class_type":"CLIPTextEncode","inputs":{"text":"active character","clip":["2",1]}},
              "4":{"class_type":"CLIPTextEncode","inputs":{"text":"active negative","clip":["2",1]}},
              "5":{"class_type":"EmptyLatentImage","inputs":{"width":512,"height":512}},
              "6":{"class_type":"KSampler","inputs":{"seed":42,"model":["2",0],"positive":["3",0],"negative":["4",0],"latent_image":["5",0]}},
              "7":{"class_type":"VAEDecode","inputs":{"samples":["6",0]}},
              "8":{"class_type":"SaveImage","inputs":{"images":["7",0]}},
              "20":{"class_type":"CLIPTextEncode","inputs":{"text":"unused character prompt"}},
              "21":{"class_type":"LoraLoader","inputs":{"lora_name":"unused-character-lora.safetensors"}},
              "22":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"unused-model.safetensors"}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal("active character", result.PositivePrompt);
        Assert.Equal("active-model.safetensors", result.ModelName);
        Assert.Collection(result.Loras, lora => Assert.Equal("active-lora.safetensors", lora.Name));
        Assert.DoesNotContain("unused", result.PositivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Loras, lora => lora.Name.Contains("unused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkflowJson_ignores_unlinked_prompt_and_lora_nodes()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["workflow"] = """
            {
              "nodes": [
                {"id":1,"type":"CLIPTextEncode","widgets_values":["linked prompt"]},
                {"id":2,"type":"CLIPTextEncode","widgets_values":["unused character prompt"]},
                {"id":3,"type":"KSampler","widgets_values":[12,"fixed",20,6,"euler","normal"],"inputs":[{"name":"positive","link":10}]},
                {"id":4,"type":"SaveImage","inputs":[{"name":"images","link":11}]},
                {"id":5,"type":"LoraLoader","widgets_values":["unused-character-lora.safetensors"]}
              ],
              "links": [[10,1,0,3,0,"CONDITIONING"],[11,3,0,4,0,"IMAGE"]]
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal("linked prompt", result.PositivePrompt);
        Assert.Empty(result.Loras);
        Assert.DoesNotContain("unused", result.PositivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromptJson_without_an_image_output_does_not_guess_from_unlinked_nodes()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["prompt"] = """
            {
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"unlinked character prompt"}},
              "2":{"class_type":"LoraLoader","inputs":{"lora_name":"unlinked-lora.safetensors"}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Null(result.PositivePrompt);
        Assert.Empty(result.Loras);
        Assert.Contains("画像出力ノード", result.Message);
    }

    [Fact]
    public void PromptJson_combines_only_the_connected_conditioning_fragments_in_input_order()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["prompt"] = """
            {
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"character"}},
              "2":{"class_type":"CLIPTextEncode","inputs":{"text":"pose"}},
              "3":{"class_type":"CLIPTextEncode","inputs":{"text":"quality"}},
              "4":{"class_type":"Reroute","inputs":{"input":["1",0]}},
              "5":{"class_type":"ConditioningConcat","inputs":{"conditioning_to":["4",0],"conditioning_from":["2",0]}},
              "6":{"class_type":"ConditioningConcat","inputs":{"conditioning_to":["5",0],"conditioning_from":["3",0]}},
              "7":{"class_type":"CLIPTextEncode","inputs":{"text":"negative"}},
              "8":{"class_type":"EmptyLatentImage","inputs":{"width":512,"height":512}},
              "9":{"class_type":"KSampler","inputs":{"seed":1,"positive":["6",0],"negative":["7",0],"latent_image":["8",0]}},
              "10":{"class_type":"VAEDecode","inputs":{"samples":["9",0]}},
              "11":{"class_type":"SaveImage","inputs":{"images":["10",0]}},
              "20":{"class_type":"CLIPTextEncode","inputs":{"text":"unused character"}}
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal("character, pose, quality", result.PositivePrompt);
        Assert.Equal("negative", result.NegativePrompt);
        Assert.DoesNotContain("unused", result.PositivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowJson_combines_only_the_connected_conditioning_fragments_in_input_order()
    {
        var metadata = new PngMetadata { IsPng = true };
        metadata.Text["workflow"] = """
            {
              "nodes": [
                {"id":1,"type":"CLIPTextEncode","widgets_values":["character"]},
                {"id":2,"type":"CLIPTextEncode","widgets_values":["pose"]},
                {"id":3,"type":"CLIPTextEncode","widgets_values":["quality"]},
                {"id":4,"type":"Reroute","inputs":[{"name":"","link":10}]},
                {"id":5,"type":"ConditioningConcat","inputs":[{"name":"conditioning_to","link":11},{"name":"conditioning_from","link":12}]},
                {"id":6,"type":"ConditioningConcat","inputs":[{"name":"conditioning_to","link":13},{"name":"conditioning_from","link":14}]},
                {"id":7,"type":"CLIPTextEncode","widgets_values":["negative"]},
                {"id":8,"type":"KSampler","widgets_values":[1,"fixed",20,6,"euler","normal"],"inputs":[{"name":"positive","link":15},{"name":"negative","link":16}]},
                {"id":9,"type":"SaveImage","inputs":[{"name":"images","link":17}]},
                {"id":20,"type":"CLIPTextEncode","widgets_values":["unused character"]}
              ],
              "links": [[10,1,0,4,0,"CONDITIONING"],[11,4,0,5,0,"CONDITIONING"],[12,2,0,5,1,"CONDITIONING"],[13,5,0,6,0,"CONDITIONING"],[14,3,0,6,1,"CONDITIONING"],[15,6,0,8,0,"CONDITIONING"],[16,7,0,8,1,"CONDITIONING"],[17,8,0,9,0,"IMAGE"]]
            }
            """;

        var result = new ComfyWorkflowParser().Parse(metadata);

        Assert.Equal("character, pose, quality", result.PositivePrompt);
        Assert.Equal("negative", result.NegativePrompt);
        Assert.DoesNotContain("unused", result.PositivePrompt, StringComparison.OrdinalIgnoreCase);
    }
}
