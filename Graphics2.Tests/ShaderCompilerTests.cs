using Graphics2.Core.Resources;

namespace Graphics2.Tests;

public class ShaderCompilerTests
{
    private const string SimpleShader = """
        struct VertexInput {
            float3 position : POSITION;
            float3 color : COLOR;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
            float3 color : COLOR;
        };

        struct Camera {
            float4x4 viewProj;
        };

        ConstantBuffer<Camera> camera;
        Texture2D<float4> tex;
        SamplerState samp;

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            output.position = mul(camera.viewProj, float4(input.position, 1.0));
            output.color = input.color;
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            return float4(input.color, 1.0);
        }
        """;

    [Fact]
    public void ShaderCompiler_ProducesSpirV()
    {
        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(SimpleShader, "vsMain", "fsMain");

        Assert.True(compiled.VertexSpirV.Length > 0);
        Assert.True(compiled.FragmentSpirV!.Length > 0);

        var vsMagic = BitConverter.ToUInt32(compiled.VertexSpirV.AsSpan(0, 4));
        var fsMagic = BitConverter.ToUInt32(compiled.FragmentSpirV.AsSpan(0, 4));
        Assert.Equal(0x07230203u, vsMagic);
        Assert.Equal(0x07230203u, fsMagic);
    }

    [Fact]
    public void ShaderCompiler_ReflectsParameters()
    {
        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(SimpleShader, "vsMain", "fsMain");

        Assert.Equal(3, compiled.Reflection.Parameters.Length);

        var camera = compiled.Reflection.Parameters[0];
        Assert.Equal("camera", camera.Name);
        Assert.Equal(0u, camera.BindingIndex);
        Assert.Equal(0u, camera.BindingSpace);
        Assert.Equal(ShaderParameterKind.UniformBuffer, camera.Kind);
        Assert.Equal(1u, camera.ElementCount);

        var tex = compiled.Reflection.Parameters[1];
        Assert.Equal("tex", tex.Name);
        Assert.Equal(1u, tex.BindingIndex);
        Assert.Equal(ShaderParameterKind.SampledImage, tex.Kind);
        Assert.Equal(1u, tex.ElementCount);
        Assert.Equal(TextureType.Texture2D, tex.TextureType);

        var samp = compiled.Reflection.Parameters[2];
        Assert.Equal("samp", samp.Name);
        Assert.Equal(2u, samp.BindingIndex);
        Assert.Equal(ShaderParameterKind.Sampler, samp.Kind);
        Assert.Equal(1u, samp.ElementCount);
    }

    [Fact]
    public void ShaderCompiler_ReflectsVertexInputs()
    {
        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(SimpleShader, "vsMain", "fsMain");

        var vs = compiled.Reflection.EntryPoints.Single(ep => ep.Stage == ShaderStage.Vertex);
        Assert.Equal("vsMain", vs.Name);
        Assert.Equal(2, vs.VertexInputs.Length);

        Assert.Equal("position", vs.VertexInputs[0].Name);
        Assert.Equal(0u, vs.VertexInputs[0].Location);
        Assert.Equal(VertexAttributeFormat.Float3, vs.VertexInputs[0].Format);

        Assert.Equal("color", vs.VertexInputs[1].Name);
        Assert.Equal(1u, vs.VertexInputs[1].Location);
        Assert.Equal(VertexAttributeFormat.Float3, vs.VertexInputs[1].Format);
    }

    [Fact]
    public void ShaderCompiler_ReflectsVolumeArray()
    {
        const string volumeShader = """
        struct VertexInput {
            float3 position : POSITION;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
        };

        Texture3D<uint> volumes[8];

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            output.position = float4(input.position, 1.0);
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            return float4(volumes[0].Load(int4(0, 0, 0, 0)).xxx, 1.0);
        }
        """;

        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(volumeShader, "vsMain", "fsMain");

        var volumes = compiled.Reflection.Parameters.Single();
        Assert.Equal("volumes", volumes.Name);
        Assert.Equal(ShaderParameterKind.SampledImage, volumes.Kind);
        Assert.Equal(8u, volumes.ElementCount);
        Assert.Equal(TextureType.Texture3D, volumes.TextureType);
    }

    [Fact]
    public void ShaderCompiler_ReflectsA2DArrayApartFromA2DImage()
    {
        const string arrayShader = """
        struct VertexInput {
            float3 position : POSITION;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
        };

        Texture2DArray layers;
        Texture2D plain;
        SamplerState samp;

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            output.position = float4(input.position, 1.0);
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            return layers.Sample(samp, float3(0.5, 0.5, 1.0)) + plain.Sample(samp, float2(0.5, 0.5));
        }
        """;

        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(arrayShader, "vsMain", "fsMain");

        Assert.Equal(TextureType.Texture2DArray, compiled.Reflection.Parameters.Single(parameter => parameter.Name == "layers").TextureType);
        Assert.Equal(TextureType.Texture2D, compiled.Reflection.Parameters.Single(parameter => parameter.Name == "plain").TextureType);
    }

    [Fact]
    public void ShaderCompiler_ReflectsFragmentEntryPoint()
    {
        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(SimpleShader, "vsMain", "fsMain");

        var fs = compiled.Reflection.EntryPoints.Single(ep => ep.Stage == ShaderStage.Fragment);
        Assert.Equal("fsMain", fs.Name);
        Assert.Empty(fs.VertexInputs);
    }

    [Fact]
    public void ShaderCompiler_ReflectsBindlessTextureArray()
    {
        const string bindlessShader = """
        struct VertexInput {
            float3 position : POSITION;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
        };

        Texture2D<float4> textures[];
        SamplerState samp;

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            output.position = float4(input.position, 1.0);
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            return textures[0].Sample(samp, float2(0.0, 0.0));
        }
        """;

        using var compiler = new ShaderCompiler();
        var compiled = compiler.Compile(bindlessShader, "vsMain", "fsMain");

        Assert.Equal(2, compiled.Reflection.Parameters.Length);

        var textures = compiled.Reflection.Parameters[0];
        Assert.Equal("textures", textures.Name);
        Assert.Equal(ShaderParameterKind.SampledImage, textures.Kind);
        Assert.Equal(0u, textures.ElementCount);

        var samp = compiled.Reflection.Parameters[1];
        Assert.Equal("samp", samp.Name);
        Assert.Equal(ShaderParameterKind.Sampler, samp.Kind);
        Assert.Equal(1u, samp.ElementCount);
    }
}
