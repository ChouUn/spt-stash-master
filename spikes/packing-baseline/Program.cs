using System;
using System.Collections.Generic;
using System.Globalization;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 0 || args[0] == "--help")
            {
                Console.WriteLine("generate --seed 20260916 --samples 24 --output corpus.json\n"
                    + "  Samples are per family: stash, container, combined (72 cases by default).\n"
                    + "replay --input corpus.json --seconds 3\n"
                    + "  Warm once, print READY, read case indices from stdin, emit JSON results.\n"
                    + "Use compare.py to run two frozen builds serially in alternating AB/BA order.");
                return 0;
            }
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                    || options.ContainsKey(args[i]))
                    throw new ArgumentException("Options require unique --name value pairs.");
                options.Add(args[i], args[i + 1]);
            }
            string Get(string name, string? fallback = null)
            {
                if (options.TryGetValue(name, out string value))
                {
                    options.Remove(name);
                    return value;
                }
                return fallback ?? throw new ArgumentException("Missing " + name);
            }
            if (args[0] == "generate")
            {
                int seed = int.Parse(Get("--seed", "20260916"), CultureInfo.InvariantCulture);
                int samples = int.Parse(Get("--samples", "24"), CultureInfo.InvariantCulture);
                string output = Get("--output");
                if (samples < 1) throw new ArgumentOutOfRangeException("--samples");
                if (options.Count != 0) throw new ArgumentException("Unknown generate option.");
                CorpusGenerator.Generate(seed, samples, output);
            }
            else if (args[0] == "replay")
            {
                string input = Get("--input");
                double seconds = double.Parse(Get("--seconds", "3"), CultureInfo.InvariantCulture);
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                    throw new ArgumentOutOfRangeException("--seconds");
                if (options.Count != 0) throw new ArgumentException("Unknown replay option.");
                ReplayRunner.Run(input, seconds);
            }
            else throw new ArgumentException("Unknown command: " + args[0]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
