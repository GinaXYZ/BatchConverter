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
        private const string AppTitle = "CSV zu JSON Konverter";
        private const string TitleSeparator = "=====================";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        static async Task Main(string[] args)
        {
            Console.Title = AppTitle;
            Console.WriteLine(AppTitle);
            Console.WriteLine(TitleSeparator);
            Console.WriteLine();

            // Fester absoluter Pfad
            var inputDir = @"C:\Users\FIA\source\repos\BatchConverter\BatchConverter\TestDaten";
            var outputDir = inputDir; // Ausgabe auch nach TestDaten

            if (!Directory.Exists(inputDir))
            {
                Console.WriteLine($"Fehler: Eingabeverzeichnis existiert nicht: {inputDir}");
                Console.WriteLine("Beliebige Taste zum Beenden...");
                Console.ReadKey(true);
                return;
            }

            // Haupt-Menüschleife: wiederholt Datei auswählen und konvertieren
            while (true)
            {
                Console.Clear();
                PrintHeader();
                Console.WriteLine($"Verzeichnis: {Path.GetFullPath(inputDir)}");
                Console.WriteLine();
                Console.WriteLine("Verfügbare CSV-Dateien:");
                Console.WriteLine();

                // CSV-Dateien jedes Mal neu laden
                var csvFiles = Directory.GetFiles(inputDir, "*.csv", SearchOption.TopDirectoryOnly)
                                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                        .ToArray();
                
                if (csvFiles.Length == 0)
                {
                    Console.WriteLine($"Keine CSV-Dateien im Ordner: {inputDir}");
                    Console.WriteLine("Beliebige Taste zum Beenden...");
                    Console.ReadKey(true);
                    return;
                }

                for (int i = 0; i < csvFiles.Length; i++)
                {   
                    var name = Path.GetFileName(csvFiles[i]);
                    Console.WriteLine($"  {i + 1,2}: {name}");
                }

                Console.WriteLine();
                Console.WriteLine("Wähle eine Datei per Nummer und drücke [Enter].");
                Console.WriteLine("Drücke [Escape], um die Anwendung zu beenden.");
                Console.WriteLine();

                var choice = ReadChoice(csvFiles.Length);
                if (choice == null)
                {
                    // ESC im Auswahlmenü -> Programm beenden
                    return;
                }

                var index = choice.Value - 1;
                var csvPath = csvFiles[index];
                var fileName = Path.GetFileName(csvPath);

                Console.Clear();
                PrintHeader($"Ausgewählte Datei: {fileName}");
                Console.WriteLine($"Verzeichnis:       {Path.GetFullPath(inputDir)}");
                Console.WriteLine();
                Console.WriteLine("Drücke [Escape] zum Abbrechen der Konvertierung.");
                Console.WriteLine();

                var cts = new CancellationTokenSource();
                var token = cts.Token;

                // Escape-Überwachung in separatem Task
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

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int processed = 0;
                int failed = 0;
                int cancelled = 0;

                Console.WriteLine($"[1/1] {fileName}");

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
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      -> Fehler: {ex.Message}");
                    failed++;
                }

                sw.Stop();
                cts.Cancel();
                await keyTask;

                Console.WriteLine();
                Console.WriteLine("Zusammenfassung:");
                Console.WriteLine($"  Verarbeitet: {processed} Datei(en)");
                Console.WriteLine($"  Fehler:      {failed} Datei(en)");
                Console.WriteLine($"  Abgebrochen: {cancelled} Datei(en)");
                Console.WriteLine($"  Dauer:       {sw.Elapsed.TotalSeconds:N2} Sekunden");
                Console.WriteLine();

                Console.WriteLine("Nochmal konvertieren? [J]a / [N]ein (oder [Escape] zum Beenden)");
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Escape || k.Key == ConsoleKey.N)
                    return;
                // bei J oder anderen Tasten: erneut Menü anzeigen
            }
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

                // Header-Indizes vorher ermitteln (effizienter)
                int idIndex = Array.FindIndex(headers, h => string.Equals(h, "Id", StringComparison.OrdinalIgnoreCase));
                int nameIndex = Array.FindIndex(headers, h => string.Equals(h, "Name", StringComparison.OrdinalIgnoreCase));
                int preisIndex = Array.FindIndex(headers, h => string.Equals(h, "Preis", StringComparison.OrdinalIgnoreCase));
                int kategorieIndex = Array.FindIndex(headers, h => string.Equals(h, "Kategorie", StringComparison.OrdinalIgnoreCase));
                int bestandIndex = Array.FindIndex(headers, h => string.Equals(h, "Bestand", StringComparison.OrdinalIgnoreCase));

                if (idIndex < 0 || nameIndex < 0 || preisIndex < 0 || kategorieIndex < 0 || bestandIndex < 0)
                {
                    throw new InvalidDataException("CSV enthält nicht alle erforderlichen Spalten (Id, Name, Preis, Kategorie, Bestand)");
                }

                for (int i = 1; i < contentLines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var line = contentLines[i];
                    var parts = line.Split(';');

                    if (parts.Length != headers.Length)
                        continue;

                    try
                    {
                        results.Add(new ProduktSimple
                        {
                            Id = int.Parse(parts[idIndex].Trim(), CultureInfo.InvariantCulture),
                            Name = parts[nameIndex].Trim(),
                            Preis = decimal.Parse(parts[preisIndex].Trim(), CultureInfo.InvariantCulture),
                            Kategorie = parts[kategorieIndex].Trim(),
                            Bestand = int.Parse(parts[bestandIndex].Trim(), CultureInfo.InvariantCulture)
                        });
                    }
                    catch
                    {
                        // Zeile überspringen bei Parse-Fehler
                        continue;
                    }
                }

                return results;
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

            var json = JsonSerializer.Serialize(export, JsonOptions);
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
            var json = JsonSerializer.Serialize(export, JsonOptions);
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

        private static int? ReadChoice(int max)
        {
            while (true)
            {
                Console.Write($"Auswahl (1-{max} oder [Escape]): ");
                
                var input = new System.Text.StringBuilder();
                
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    
                    if (key.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine();
                        return null;
                    }
                    
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        if (input.Length > 0 && int.TryParse(input.ToString(), out int num) && num >= 1 && num <= max)
                        {
                            return num;
                        }
                        Console.WriteLine("  -> Ungültige Eingabe.");
                        break;
                    }
                    
                    if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                    {
                        input.Length--;
                        Console.Write("\b \b");
                    }
                    else if (char.IsDigit(key.KeyChar))
                    {
                        input.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                }
            }
        }

        private static void PrintHeader(string? subtitle = null)
        {
            Console.WriteLine(AppTitle);
            Console.WriteLine(TitleSeparator);
            Console.WriteLine();
            if (!string.IsNullOrEmpty(subtitle))
            {
                Console.WriteLine(subtitle);
                Console.WriteLine();
            }
        }
    }
}

