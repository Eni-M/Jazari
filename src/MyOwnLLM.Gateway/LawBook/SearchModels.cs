using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyOwnLLM.Gateway;

/// <summary>The law book search's two small models: one turns text into a vector, one scores a question with a passage.
/// An interface only so the tests can stand in for them without 57 MB of model files.</summary>
public interface ISearchModels
{
    /// <summary>One L2-normalized vector per text. Questions should be passed through <see cref="OnnxSearchModels.AsQuestion"/>.</summary>
    float[][] Embed(IReadOnlyList<string> texts);

    /// <summary>How well each passage answers the question: higher is better, about 0 is the border.</summary>
    float[] Rerank(string question, IReadOnlyList<string> passages);
}

/// <summary>
/// bge-small-en-v1.5 for vectors and ms-marco-MiniLM-L12-v2 for reranking, both int8 ONNX on the CPU, read through the same
/// WordPiece vocabulary. Each in its own folder under <see cref="GatewayOptions.SearchModelsDir"/>, as in spike/models.
/// </summary>
public sealed class OnnxSearchModels : ISearchModels, IDisposable
{
    public const string EmbedderFolder = "bge-small-en-v1.5", RerankerFolder = "ms-marco-MiniLM-L12-v2";
    const string EmbedderFile = "model_quantized.onnx", RerankerFile = "model_quint8_avx2.onnx";
    public const int MaxTokens = 256; // passages are at most 1,000 characters, about 250 tokens: no need for the models' 512
    const string QuestionPrefix = "Represent this sentence for searching relevant passages: "; // bge v1.5's prefix for queries

    readonly InferenceSession _embedder, _reranker;
    readonly WordPiece _embedTokens, _rerankTokens;

    OnnxSearchModels(InferenceSession embedder, WordPiece embedTokens, InferenceSession reranker, WordPiece rerankTokens) =>
        (_embedder, _embedTokens, _reranker, _rerankTokens) = (embedder, embedTokens, reranker, rerankTokens);

    public static string AsQuestion(string question) => QuestionPrefix + question;

    /// <summary>Both models, or null when a file is missing or won't load: the search then runs on words alone.</summary>
    public static OnnxSearchModels? Load(string dir)
    {
        string embedder = Path.Combine(dir, EmbedderFolder), reranker = Path.Combine(dir, RerankerFolder);
        string[] files = [Path.Combine(embedder, EmbedderFile), Path.Combine(embedder, "vocab.txt"), Path.Combine(reranker, RerankerFile), Path.Combine(reranker, "vocab.txt")];
        if (!files.All(File.Exists)) return null;
        InferenceSession? e = null;
        try
        {
            // Half the cores, as llama-server gets: the laptop stays usable, and a search is a fraction of a second either way.
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2), InterOpNumThreads = 1 };
            e = new InferenceSession(files[0], options);
            return new OnnxSearchModels(e, WordPiece.Load(files[1]), new InferenceSession(files[2], options), WordPiece.Load(files[3]));
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            e?.Dispose();
            return null;
        }
    }

    public float[][] Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        var output = Run(_embedder, texts.Select(t => _embedTokens.Encode(t, null, MaxTokens)).ToList(), "last_hidden_state");
        var (batch, hidden) = (output.Dimensions[0], output.Dimensions[2]);
        var vectors = new float[batch][];
        for (var i = 0; i < batch; i++)
        {
            var v = new float[hidden];
            for (var j = 0; j < hidden; j++) v[j] = output[i, 0, j]; // the [CLS] token's state, as bge pools
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0) for (var j = 0; j < hidden; j++) v[j] /= norm;
            vectors[i] = v;
        }
        return vectors;
    }

    public float[] Rerank(string question, IReadOnlyList<string> passages)
    {
        if (passages.Count == 0) return [];
        var output = Run(_reranker, passages.Select(p => _rerankTokens.Encode(question, p, MaxTokens)).ToList(), "logits");
        return [.. Enumerable.Range(0, passages.Count).Select(i => output[i, 0])];
    }

    /// <summary>One batch, padded to its longest: ids, mask and token types for whichever of them the model takes.</summary>
    static Tensor<float> Run(InferenceSession session, List<(long[] Ids, long[] TypeIds)> encoded, string outputName)
    {
        var (batch, length) = (encoded.Count, encoded.Max(e => e.Ids.Length));
        var ids = new DenseTensor<long>([batch, length]);
        var mask = new DenseTensor<long>([batch, length]);
        var types = new DenseTensor<long>([batch, length]);
        for (var i = 0; i < batch; i++)
            for (var j = 0; j < encoded[i].Ids.Length; j++)
                (ids[i, j], mask[i, j], types[i, j]) = (encoded[i].Ids[j], 1, encoded[i].TypeIds[j]);
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in session.InputMetadata.Keys)
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, name switch { "input_ids" => ids, "attention_mask" => mask, "token_type_ids" => types, _ => throw new InvalidOperationException($"unknown model input {name}") }));
        using var results = session.Run(inputs);
        var result = results.FirstOrDefault(r => r.Name == outputName) ?? results.First();
        return result.AsTensor<float>().Clone();
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _reranker.Dispose();
    }
}
