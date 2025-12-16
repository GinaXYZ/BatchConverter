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
            Console.WriteLine("CSV zu JSON Konverter");
            Console.WriteLine(new string('=', 21));
            Console.WriteLine();

            var (inputDir, outputDir) = ParseArguments(args);
            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
            {
                Console.WriteLine("Fehler: Ungültiges oder fehlendes Eingabeverzeichnis. Benutze --in\"<Pfad>\"");
                return;
            }
            outputDir ??= inputDir;

            Console.WriteLine($"Eingabeverzeichnis: {Path.GetFullPath(inputDir)}");
            Console.WriteLine($"Ausgabeverzeichnis: {Path.GetFullPath(outputDir)}");
            Console.WriteLine();
            Console.WriteLine("Drücke [Escape] zum Abbrechen.");
            Console.WriteLine();

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            // Tastaturüberwachung (Escape zum Abbrechen)
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
                    Thread.Sleep(100);
                }
            });

            var progress = new Progress<string>(msg =>
            {
                Console.WriteLine(msg);
            });

            var csvFiles = Directory.GetFiles(inputDir, "*.csv", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"Gefunden: {csvFiles.Length} CSV-Datei(en)");
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
                Console.WriteLine($"[{i + 1}/{csvFiles.Length}] {fileName}");

                try
                {
                    await VerarbeiteDateiAsync(csvPath, outputDir, progress, token);
                    Console.WriteLine($"      -> {Path.GetFileNameWithoutExtension(fileName)}.json OK");
                    processed++;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("      -> Abgebrochen");
                    cancelled++;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      -> Fehler: {ex.Message}");
                    failed++;
                }

                Console.WriteLine();
            }

            sw.Stop();
            Console.WriteLine("Zusammenfassung:");
            Console.WriteLine($"  Verarbeitet: {processed} Datei(en)");
            Console.WriteLine($"  Fehler:      {failed} Datei(en)");
            Console.WriteLine($"  Abgebrochen: {cancelled} Datei(en)");
            Console.WriteLine($"  Dauer:       {sw.Elapsed.TotalSeconds:N2} Sekunden");

            // Ensure keyboard task stops
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

            static string TrimQuotes(string s) => s?.Trim().Trim('\'', '\"').Trim();
        }

        private static async Task VerarbeiteDateiAsync(string csvPath, string outputDir, IProgress<string> progress, CancellationToken token)
        {
            progress.Report("      Lese CSV-Datei...");
            var allLines = await File.ReadAllLinesAsync(csvPath, token);

            // Filter: Kommentarzeilen und leere Zeilen entfernen
            var contentLines = allLines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l => !l.TrimStart().StartsWith("#"))
                .ToArray();

            progress.Report($"      {contentLines.Length} Zeilen gelesen");

            if (contentLines.Length == 0)
            {
                // Leere Datei -> leeres Ergebnis schreiben
                await WriteEmptyResultAsync(csvPath, outputDir, progress, token);
                return;
            }

            progress.Report("      Parse Daten...");

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
                        // ungültige Zeile überspringen
                        continue;
                    }

                    try
                    {
                        var dict = headers.Zip(parts, (h, v) => new { h, v })
                                          .ToDictionary(x => x.h, x => x.v.Trim());

                        int id = int.Parse(Get(dict, "Id"), CultureInfo.InvariantCulture);
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
                        // Zeile überspringen bei Parse-Fehler
                        continue;
                    }
                }

                return results;

                static string Get(Dictionary<string, string> d, string key)
                {
                    // tolerant: suche case-insensitive
                    var k = d.Keys.FirstOrDefault(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
                    return k != null ? d[k] : string.Empty;
                }
            }, token);

            progress.Report($"      {produkte.Count} Produkte geparst");

            progress.Report("      Schreibe JSON-Datei...");
            var export = new
            {
                quelldatei = Path.GetFileName(csvPath),
                konvertiertAm = DateTime.UtcNow.ToString("o"),
                anzahlDatensaetze = produkte.Count,
                produkte = produkte.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    preis = p.Preis,
                    kategorie = p.Kategorie,
                    bestand = p.Bestand
                }).ToList()
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(export, options);
            Directory.CreateDirectory(outputDir);
            var outFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(csvPath) + ".json");
            await File.WriteAllTextAsync(outFile, json, token);
        }

        private static async Task WriteEmptyResultAsync(string csvPath, string outputDir, IProgress<string> progress, CancellationToken token)
        {
            var export = new
            {
                quelldatei = Path.GetFileName(csvPath),
                konvertiertAm = DateTime.UtcNow.ToString("o"),
                anzahlDatensaetze = 0,
                produkte = new object[0]
            };
            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(outputDir);
            var outFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(csvPath) + ".json");
            await File.WriteAllTextAsync(outFile, json, token);
            progress.Report($"      -> {Path.GetFileName(outFile)} (leer) geschrieben");
        }

        // kleines internes DTO zur einfachen Serialisierung
        private class ProduktSimple
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public decimal Preis { get; set; }
            public string Kategorie { get; set; } = string.Empty;
            public int Bestand { get; set; }
        }
    }
}

