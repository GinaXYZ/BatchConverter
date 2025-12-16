using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BatchConverter
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Batch CSV to JSON Converter");
            Console.WriteLine(new string('=', 21));
            Console.WriteLine();

            var (inputDir, outputDir) = ParseArguments(args);
            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
            {
                Console.WriteLine("Invalid or missing input directory.");
                return;
            }
            outputDir ??= inputDir;

            Console.WriteLine($"Input Directory: {inputDir}");
            Console.WriteLine($"Output Directory: {outputDir}");
            Console.WriteLine();
            Console.WriteLine("Press [Escape] to cancel.");
            Console.WriteLine();

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var keyTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            cts.Cancel();
                            break;
                        }

                    }
                }
                Thread.Sleep(100);
            });

            var progress = new Progress<string>(msg =>
            {
                Console.WriteLine(msg);
            });

            var csvFiles = Directory.GetFiles(inputDir, "*.csv", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"Found {csvFiles.Length} CSV files to process.");
            Console.WriteLine();

            int processed = 0;
            int failed = 0;
            int cancelled = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < csvFiles.Length; i++)
            {
                if (token.IsCancellationRequested) break;

                var csvPath = csvFiles[i];
                var fileName = Path.GetFileName(csvPath);
                Console.WriteLine($"[{i + 1}/{csvFiles.Length}] Processing '{fileName}'...");

                try
                {
                    await VerarbeiteDateiAsync(csvPath, outputDir, progress, token);
                    Console.WriteLine($"[{i + 1}/{csvFiles.Length}] Successfully processed '{fileName}'.");
                    processed++;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Operation cancelled by user.");
                    cancelled++;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ->Error {ex.Message}");
                    failed++;
                }
                Console.WriteLine();
            }
            sw.Stop();
            Console.WriteLine("Summary: ");
            Console.WriteLine($"    Processed: {processed}");
            Console.WriteLine($"    Failed:    {failed}");
            Console.WriteLine("    Cancelled: {cancelled}");
            Console.WriteLine($"    Time Elapsed: {sw.Elapsed}");

            cts.Cancel();
            await keyTask;
        }

        private static (string input, string output) ParseArguments(string[] args)
        {
            string input = null;
            string output = null;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].Trim();
                if (a.StartsWith("--in", StringComparison.OrdinalIgnoreCase))
                {
                    var val = a.Length > 4 ? a.Substring(4) : null;
                    if (string.IsNullOrWhiteSpace(val) && i + 1 < args.Length)
                    {
                        val = args[++i];
                    }
                    input = TrimQuotes(val);
                }
                else if (a.StartsWith("--out", StringComparison.OrdinalIgnoreCase))
                {
                    var val = a.Length > 5 ? a.Substring(5) : null;
                    if (string.IsNullOrWhiteSpace(val) && i + 1 < args.Length)
                    {
                        val = args[++i];
                    }
                    output = TrimQuotes(val);
                }
            }
            return (input, output);
            static string TrimQuotes(string s) => s?.Trim().Trim('\"').Trim();
        }

        private static async Task VerarbeiteDateiAsync(string csvPath, string outputDir)
        {
            progress.Report("   Lese CSV-Datei...");
            var allLines = await File.ReadAllLinesAsync(csvPath, token);

            var contentLines = allLines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l => !l.TrimStart().StartsWith("#"))
                .ToArray();

            progress.Report($"   Gefundene Datenzeilen: {contentLines.Length}");

            if (contentLines.Length == 0)
            {
                await WriteEmptryResultAsync(csvPath, outputDir, progress, token);
                return;
            }

            progress.Report("   Verarbeite Datenzeilen...");

            var produkte = await Task.Run(() =>
            {
            token.ThrowIfCancellationRequested();

            var results = new List<ProduktSimple>();
            string headerLine = contentLines[0];
            var headers = headerLine.Split(';').Select(h => h.Trim()).ToArray();

            for (int i = 1; i < contentLines.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var line = contentLines[i];
                var parts = line.Split(';');

                if (parts.Length != headers.Length)
                {
                    continue;
                }

                try
                {
                    var dict = headers.Zip(parts, (h, v) => new { h, v })
                                      .ToDictionary(x => x.h, x => x.v.Trim());
                    int id = int.Parse(dict["ID"], CultureInfo.InvariantCulture);
                    string name = Get(dict, "Name");
                    decimal preis = decimal.Parse(Get(dict, "Preis"), CultureInfo.InvariantCulture);
                    string kategorie = Get(dict, "Kategorie");
                    int bestand = int.Parse(Get(dict, "Bestand"), CultureInfo.InvariantCulture);

                    results.Add(new ProduktSimple
                    {
                        Id = id,
                        Name = name,
                        Preis = preis,
                        Kategorie = kategorie,
                        Bestand = bestand
                    });
                }
                catch
                {
                    continue;
                }

                return results;

                static string Get(Dictionary<string, string> d, string key)
                {
                    // tolerant: suche case-insensitive
                    var k = d.Keys.FirstOrDefault(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
                    return k != null ? d[k] : string.Empty;
                }
            }, token);

            progress.Report($"   {produkte.Count} Produkte verarbeitet.");

            progress.Report("   Schreibe JSON-Ausgabedatei...");
            var export = new
            {

            }
        });
        }
    }
}

