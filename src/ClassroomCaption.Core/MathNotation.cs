using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassroomCaption.Core;

public sealed record MathRun(string Text,bool Formula);
public static class MathNotation
{
    private static readonly Regex explicitMath=new(@"\$\$(?<tex>[\s\S]+?)\$\$|\$(?<tex>[^$\r\n]+)\$|\\\((?<tex>[\s\S]+?)\\\)|\\\[(?<tex>[\s\S]+?)\\\]",RegexOptions.Compiled);
    private const string Atom=@"(?:[A-Za-zα-ωΑ-Ω][A-Za-zα-ωΑ-Ω0-9]*(?:_(?:\{[A-Za-zα-ω0-9]+\}|[A-Za-zα-ω0-9]+))?(?:\^(?:\{[0-9]+\}|[0-9]+))?|[0-9]+(?:\.[0-9]+)?)";
    private const string Operand=@"(?:"+Atom+@"|\([A-Za-zα-ωΑ-Ω0-9_{}^+* .−-]+\))";
    private static readonly Regex fraction=new(@"(?<![A-Za-zα-ωΑ-Ω0-9_/.:])(?<a>"+Operand+@")\s*(?:/|÷|除以)\s*(?<b>"+Operand+@")(?![A-Za-zα-ωΑ-Ω0-9_/.])",RegexOptions.Compiled);
    private static readonly Regex superscript=new(@"(?<a>[A-Za-zα-ωΑ-Ω][A-Za-zα-ωΑ-Ω0-9]*(?:_[A-Za-z0-9]+)?)\s*(?:\^(?<p>[0-9]+)|(?<p>[²³]))",RegexOptions.Compiled);
    private static readonly Regex subscript=new(@"[A-Za-zα-ωΑ-Ω]_(?:\{[A-Za-zα-ωΑ-Ω0-9]+\}|[A-Za-zα-ωΑ-Ω0-9]+)",RegexOptions.Compiled);
    private static readonly Regex function=new(@"(?<![A-Za-z])(?<f>sin|cos|tan|arctan|sqrt)\s*(?<a>\([A-Za-zα-ωΑ-Ω0-9_+* .−-]+\)|[A-Za-zα-ωΑ-Ω][A-Za-zα-ωΑ-Ω0-9_]*)",RegexOptions.Compiled);
    private static readonly Dictionary<string,string> symbols=new() { ["alpha"]="α",["beta"]="β",["gamma"]="γ",["delta"]="δ",["theta"]="θ",["lambda"]="λ",["mu"]="μ",["nu"]="ν",["xi"]="ξ",["zeta"]="ζ",["rho"]="ρ",["phi"]="φ",["psi"]="ψ",["omega"]="ω",["pi"]="π" };
    private const string SpokenLetter = @"(?:икс|игрек|вэ|т[еэ]та)";
    private const RegexOptions SpokenOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex SpokenPair = new(
        @"\b(?<a>" + SpokenLetter + @")\s+(?:(?<relation>с\s+(?:нижним\s+)?индексом|умножить\s+на|от)\s+)?(?<b>" + SpokenLetter + @")\b",
        SpokenOptions);
    private static readonly Regex FunctionCue = new(@"\bфункци(?:я|и|ю|ей)\s*$", SpokenOptions);
    private static readonly Regex IndexCue = new(
        @"^\s*,\s*где\s+(?<index>" + SpokenLetter + @")\s*[-–—]\s*(?:нижний\s+)?индекс\b", SpokenOptions);

    private static string NormalizeSpokenPair(Match pair, string text)
    {
        static string Symbol(string name) => name.ToLowerInvariant() switch
        {
            "икс" => "x",
            "игрек" => "y",
            "вэ" => "v",
            _ => "θ"
        };

        var first = Symbol(pair.Groups["a"].Value);
        var second = Symbol(pair.Groups["b"].Value);
        var relation = pair.Groups["relation"].Value.ToLowerInvariant();
        if (relation.StartsWith("с", StringComparison.Ordinal)) return first + "_{" + second + "}";
        if (relation.StartsWith("умножить", StringComparison.Ordinal)) return first + "·" + second;
        if (relation == "от" || FunctionCue.IsMatch(text[..pair.Index])) return $"{first}({second})";

        var indexCue = IndexCue.Match(text[(pair.Index + pair.Length)..]);
        if (indexCue.Success && Symbol(indexCue.Groups["index"].Value) == second)
            return first + "_{" + second + "}";
        return first + second;
    }

    public static string Normalize(string text)
    {
        text = SpokenPair.Replace(text, pair => NormalizeSpokenPair(pair, text));
        text=text.Replace("²","^2").Replace("³","^3");
        // Only explicit names/symbols; never infer a missing exponent, root or denominator.
        text=Regex.Replace(text,@"(?<![A-Za-z])(?:sinus|sine|cosine|cosinus)(?![A-Za-z])",m=>m.Value.StartsWith("cos",StringComparison.OrdinalIgnoreCase)?"cos":"sin",RegexOptions.IgnoreCase);
        foreach(var (name,symbol) in symbols)text=Regex.Replace(text,@"(?<![A-Za-z])"+name+@"(?![A-Za-z])",symbol,RegexOptions.IgnoreCase);
        return text;
    }
    public static bool HasIncompleteMath(string text)
    {
        if(text.Count(c=>c=='$')%2!=0)return true;
        if(!text.Contains('\\'))return false;
        var depth=0;foreach(var c in text){if(c=='{')depth++;if(c=='}')depth--;}
        return depth!=0;
    }
    public static string ToLatex(string expression)
    {
        foreach(var (name,symbol) in symbols)expression=expression.Replace(symbol,"\\"+name+" ");
        expression=expression.Replace("−","-").Replace("×",@"\cdot ");
        return expression;
    }
    public static IReadOnlyList<MathRun> Parse(string text)
    {
        var runs=new List<MathRun>();var position=0;
        foreach(Match match in explicitMath.Matches(text))
        {
            Plain(text[position..match.Index],runs);
            var tex=match.Groups["tex"].Value;
            runs.Add(new(tex.Length<=512?"$"+tex+"$":match.Value,tex.Length<=512));position=match.Index+match.Length;
        }
        Plain(text[position..],runs);return runs;
    }
    private static void Plain(string text,List<MathRun> runs)
    {
        text=Normalize(text);
        // Raw TeX from a model is supported without requiring $ delimiters as well.
        if(text.Contains('\\')&&!HasIncompleteMath(text))
        {
            var start=text.IndexOf('\\');var end=start;var depth=0;
            while(end<text.Length)
            {
                var c=text[end];if(c=='{')depth++;if(c=='}')depth--;
                if(depth==0&&end>start&&(c>127||c is '。' or '，'))break;
                end++;
            }
            if(end>start&&end-start<=512){Plain(text[..start],runs);runs.Add(new("$"+text[start..end].TrimEnd()+"$",true));Plain(text[end..],runs);return;}
        }
        var position=0;
        foreach(Match match in fraction.Matches(text))
        {
            // "sin x/y" has ambiguous scope; do not silently turn it into sin(x/y).
            if(Regex.IsMatch(text[..match.Index],@"(?:sin|cos|tan|arctan)\s*$",RegexOptions.IgnoreCase))continue;
            Simple(text[position..match.Index],runs);
            var a=match.Groups["a"].Value;var b=match.Groups["b"].Value;
            if(a.StartsWith('(')&&a.EndsWith(')'))a=a[1..^1];
            if(b.StartsWith('(')&&b.EndsWith(')'))b=b[1..^1];
            runs.Add(new(@"$\frac{"+ToLatex(a)+"}{"+ToLatex(b)+"}$",true));position=match.Index+match.Length;
        }
        Simple(text[position..],runs);
    }
    private static void Simple(string text,List<MathRun> runs)
    {
        var position=0;
        foreach(Match match in superscript.Matches(text))
        {
            Subscripts(text[position..match.Index],runs);
            var power=match.Groups["p"].Value.Replace("²","2").Replace("³","3");
            runs.Add(new("$"+ToLatex(match.Groups["a"].Value)+"^{"+power+"}$",true));position=match.Index+match.Length;
        }
        Subscripts(text[position..],runs);
    }
    private static void Subscripts(string text,List<MathRun> runs)
    {
        var position=0;
        foreach(Match match in subscript.Matches(text))
        {
            Functions(text[position..match.Index],runs);runs.Add(new("$"+ToLatex(match.Value)+"$",true));position=match.Index+match.Length;
        }
        Functions(text[position..],runs);
    }
    private static void Functions(string text,List<MathRun> runs)
    {
        var position=0;
        foreach(Match match in function.Matches(text))
        {
            Text(text[position..match.Index],runs);var name=match.Groups["f"].Value;var arg=match.Groups["a"].Value;
            if(name=="sqrt"){if(arg.StartsWith('('))arg=arg[1..^1];runs.Add(new(@"$\sqrt{"+ToLatex(arg)+"}$",true));}
            else runs.Add(new("$\\"+name+" "+ToLatex(arg)+"$",true));
            position=match.Index+match.Length;
        }
        Text(text[position..],runs);
    }
    private static void Text(string text,List<MathRun> runs)
    {
        var it=StringInfo.GetTextElementEnumerator(text);
        while(it.MoveNext())runs.Add(new(it.GetTextElement(),false));
    }
}
