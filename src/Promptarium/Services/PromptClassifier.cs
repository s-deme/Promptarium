using System.Text.RegularExpressions;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class PromptClassifier
{
    public static readonly IReadOnlyList<string> Categories = [
        "未分類", "キャラクター", "ポーズ／行動", "シチュエーション／環境", "スタイル／品質", "構図／カメラ"
    ];

    private static readonly string[] CharacterTerms = ["girl", "boy", "woman", "man", "female", "male", "hair", "eyes", "skin", "dress", "shirt", "character", "1girl", "1boy", "2girls"];
    private static readonly string[] PoseTerms = ["standing", "sitting", "lying", "kneeling", "running", "jumping", "looking", "arms", "hands", "pose", "smile", "expression"];
    private static readonly string[] SituationTerms = ["outdoors", "indoors", "room", "bedroom", "classroom", "beach", "street", "forest", "night", "sunset", "rain", "snow", "city", "background"];
    private static readonly string[] StyleTerms = ["masterpiece", "best quality", "high quality", "anime", "realistic", "illustration", "cinematic", "detailed", "8k", "low quality"];
    private static readonly string[] CameraTerms = ["close-up", "close up", "wide shot", "full body", "from above", "from below", "depth of field", "lens", "angle", "composition", "portrait"];

    public List<PromptTag> Classify(string? prompt, string kind = "positive")
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return [];
        }

        var tokens = Split(prompt);
        return tokens.Select((token, index) => new PromptTag
        {
            PromptKind = kind,
            Ordinal = index,
            RawText = token,
            NormalizedText = Normalize(token),
            Category = ClassifyToken(token),
            Source = "automatic"
        }).ToList();
    }

    public static string Normalize(string token) => Regex.Replace(token.Trim().ToLowerInvariant().Replace('_', ' '), "\\s+", " ");

    private static IEnumerable<string> Split(string prompt)
    {
        var current = new List<char>();
        var depth = 0;
        foreach (var character in prompt)
        {
            if (character is '(' or '[' or '{') depth++;
            if (character is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            if (character == ',' && depth == 0)
            {
                var token = new string(current.ToArray()).Trim();
                if (!string.IsNullOrWhiteSpace(token)) yield return token;
                current.Clear();
            }
            else
            {
                current.Add(character);
            }
        }

        var last = new string(current.ToArray()).Trim();
        if (!string.IsNullOrWhiteSpace(last)) yield return last;
    }

    private static string ClassifyToken(string token)
    {
        var normalized = Normalize(token);
        if (ContainsAny(normalized, CharacterTerms)) return "キャラクター";
        if (ContainsAny(normalized, PoseTerms)) return "ポーズ／行動";
        if (ContainsAny(normalized, SituationTerms)) return "シチュエーション／環境";
        if (ContainsAny(normalized, StyleTerms)) return "スタイル／品質";
        if (ContainsAny(normalized, CameraTerms)) return "構図／カメラ";
        return "未分類";
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
