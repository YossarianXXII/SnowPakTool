using System.Reflection;
using System.Text.Json;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object Difficulty(string value) => Enum.Parse(T("ProgressionDifficulty"), value);
    private static object? Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(obj, args);
    private static double[] Weights(int position, int count, int tier, string difficulty) => (double[])T("TruckTireRandomization").GetMethod("Weights")!.Invoke(null, new object[] { position, count, tier, Difficulty(difficulty) })!;
    private static int Draw(double[] weights, double roll) => (int)T("TruckTireRandomization").GetMethod("Draw")!.Invoke(null, new object[] { weights, roll })!;
    private static double Mean(double[] weights) => weights.Select((weight, index) => weight * index / 100).Sum();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    private static object Property(object obj, string name) => obj.GetType().GetProperty(name)!.GetValue(obj)!;
    [STAThread] private static void Main()
    {
        foreach (var count in new[] { 1, 2, 19 })
            foreach (var mode in new[] { "Easy", "Normal", "Hard" })
                for (var tier = 0; tier < 5; tier++)
                    if (!Weights(0, count, tier, mode).SequenceEqual(new double[] { 99, 1, 0, 0 })) throw new Exception("first-region odds");
        Check(true, "first region is exactly 99% Highway / 1% All-terrain for every tier/mode, including single-region orders");
        foreach (var mode in new[] { "Easy", "Normal", "Hard" })
            for (var tier = 0; tier < 5; tier++)
            {
                var previous = -1.0;
                for (var position = 0; position < 19; position++)
                {
                    var weights = Weights(position, 19, tier, mode);
                    if (weights.Any(weight => weight < 0) || Math.Abs(weights.Sum() - 100) > 0.000001 || Mean(weights) < previous - 0.000001) throw new Exception("invalid or non-monotonic probability curve");
                    previous = Mean(weights);
                }
            }
        Check(true, "all curves stay normalized and steadily improve tires as progression advances");
        for (var position = 1; position < 19; position++)
        {
            for (var tier = 0; tier < 5; tier++)
                if (!(Mean(Weights(position, 19, tier, "Easy")) > Mean(Weights(position, 19, tier, "Normal")) && Mean(Weights(position, 19, tier, "Normal")) > Mean(Weights(position, 19, tier, "Hard")))) throw new Exception("difficulty ordering");
            if (Mean(Weights(position, 19, 4, "Normal")) <= Mean(Weights(position, 19, 0, "Normal"))) throw new Exception("region difficulty contribution");
        }
        Check(true, "tire difficulty and region tier both influence odds independently of progression position");
        Check(Weights(18, 19, 4, "Easy")[3] == 70 && Weights(18, 19, 4, "Normal")[3] == 45 && Weights(18, 19, 4, "Hard")[3] == 30,
            "late S regions retain 70/45/30% Mudtires chances for Easy/Normal/Hard");
        var first = Weights(0, 19, 4, "Hard");
        var draws = Enumerable.Range(0, 10000).Select(index => Draw(first, (index + 0.5) / 10000)).ToArray();
        Check(draws.Count(category => category == 0) == 9900 && draws.Count(category => category == 1) == 100, "weighted draw implements the exact first-region 99/1 split");
        var random = new Random(42);
        var middle = Weights(9, 19, 2, "Normal");
        Check(Enumerable.Range(0, 500).Select(_ => Draw(middle, random.NextDouble())).Distinct().Count() == 4, "independent per-truck draws produce multiple tire categories within the same region/map");
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var path = Path.Combine(fixture, "rules.json");
        var store = Activator.CreateInstance(T("TruckRandomizationRuleStore"), path)!;
        File.WriteAllText(path, "{\"TireDifficulty\":0,\"Obtainable\":[\"Custom rule\"],\"Missions\":[]}");
        var rules = Call(store, "Load")!;
        Check(Property(rules, "Obtainable") is List<string> notes && notes.Single() == "Custom rule", "legacy difficulty is ignored without losing custom notes");
        Call(store, "Save", rules);
        var saved = File.ReadAllText(path);
        Check(!saved.Contains("TireDifficulty") && saved.Contains("Custom rule"), "saving rules removes the obsolete difficulty setting and preserves notes");
        var overloads = T("ObtainableTruckRandomizer").GetMethods().Where(method => method.Name == "Randomize").ToArray();
        Check(overloads.Length == 2 && overloads.All(method => method.GetParameters().Single(parameter => parameter.Name == "tireDifficulty").DefaultValue!.Equals(Difficulty("Hard"))), "both randomizer entry points default tire calculations to Hard");
        File.WriteAllText(path, "{\"Obtainable\":[],\"Missions\":[]}");
        using var tab = (Control)Activator.CreateInstance(T("TruckRandomizationTab"), BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { null, store }, null)!;
        tab.Size = new Size(1120, 740); tab.CreateControl(); tab.PerformLayout();
        var choices = All(tab).OfType<RadioButton>().ToArray();
        Check(choices.Length == 0 && !All(tab).Any(control => control.Text == "Tire difficulty"), "tire difficulty controls are removed");
        var list = (System.Collections.IList)Property(Call(store, "Load")!, "Obtainable");
        Check(list.Count == 0, "empty editable rule lists stay empty");
        using var bitmap = new Bitmap(tab.Width, tab.Height); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, "tire-difficulty-preview.png"));
        Console.WriteLine("All progression/tire-difficulty checks passed using isolated settings.");
    }
}
