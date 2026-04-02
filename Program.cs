using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 250 * 1024 * 1024; // 250MB
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();

bool IsTooDark(string color)
{
    if (string.IsNullOrEmpty(color)) return false;
    color = color.ToLower().Trim();
    if (color == "black" || color == "#000" || color == "#000000" || color.Contains("rgb(0,0,0)")) return true;
    if (color == "white" || color == "#fff" || color == "#ffffff") return false;

    try {
        int r = 0, g = 0, b = 0;
        if (color.StartsWith("#")) {
            string hex = color.Substring(1);
            if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 6) {
                r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            }
        } else if (color.StartsWith("rgb")) {
            var match = System.Text.RegularExpressions.Regex.Match(color, @"rgb\((\d+),\s*(\d+),\s*(\d+)\)");
            if (match.Success) {
                r = int.Parse(match.Groups[1].Value);
                g = int.Parse(match.Groups[2].Value);
                b = int.Parse(match.Groups[3].Value);
            }
        } else return false;

        double brightness = 0.299 * r + 0.587 * g + 0.114 * b;
        return brightness < 80;
    } catch { return false; }
}

int processingCount = 0;

app.MapPost("/upload", async (IFormFile file) => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    Action<string> log = (msg) => {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch {}
    };

    if (System.Threading.Interlocked.CompareExchange(ref processingCount, 1, 0) != 0) {
        log("REJECTED: Another conversion process is already running.");
        return Results.Problem("Şu anda devam eden bir harita dönüştürme işlemi var. Sunucu aynı anda sadece 1 işlemi destekler. Lütfen bitmesini bekleyin.", statusCode: 409);
    }

    try {
        log("=== NEW UPLOAD REQUEST STARTED ===");
        if (file == null || file.Length == 0) {
        log("ERROR: No file uploaded.");
        return Results.BadRequest("No file uploaded.");
    }

    log($"Received file: {file.FileName} ({file.Length} bytes)");

    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
    var outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "output");
    
    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

    var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
    var filePath = Path.Combine(uploadsFolder, fileName);
    var outputFileName = fileName.Replace(".dwg", ".svg").Replace(".DWG", ".svg");
    var outputPath = Path.Combine(outputFolder, outputFileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    log($"File saved to: {filePath}");

    // TRIGGER LibreDWG (dwg2svg.exe)
    var exePath = @"C:\Users\SinanYalçın\Ignis\DWG_Viewer\libredwg_tool\dwg2SVG.exe";
    
    log($"Starting conversion with LibreDWG.");
    log($"Command: \"{exePath}\" --mspace \"{filePath}\"");

    try {
        string tempSvgPath = outputPath + ".tmp.svg";
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{exePath}\" --mspace \"{filePath}\" > \"{tempSvgPath}\"\"",
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) {
            log("ERROR: Failed to start conversion process cmd.exe.");
            return Results.Problem("Failed to start conversion process.");
        }
        
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        log($"LibreDWG conversion completed. ExitCode: {process.ExitCode}");

        if (!File.Exists(tempSvgPath) || new FileInfo(tempSvgPath).Length == 0)
        {
            log($"CLI-ERR: SVG generation failed or output is empty. STDERR: {error}");
            return Results.Problem("SVG generation failed or file is empty.");
        }
        
        log($"LibreDWG conversion completed. Temp SVG created at: {tempSvgPath} ({new FileInfo(tempSvgPath).Length} bytes)");

        string output = await File.ReadAllTextAsync(tempSvgPath);
        File.Delete(tempSvgPath);

        log("Read temp SVG content. Validating <svg tag...");
        if (!string.IsNullOrEmpty(output) && output.Contains("<svg"))
        {
            var swOpt = System.Diagnostics.Stopwatch.StartNew();
            long originalSize = output.Length;
            log($"SVG is valid. Starting optimization... (Original Size: {originalSize} bytes)");
            // --- ROBUST OPTIMIZATION START ---
            // 1. Basic structure cleanup (Ensuring main group and styles)
            var finalSvg = output;
            
            // SUPER SAFE MINIFICATION (Reduces 93MB -> ~30MB)
            // 1. Remove all XML comments
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @"<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            // 2. Reduce decimal precision from 6+ digits to 2 digits (e.g. 1909.218561 -> 1909.21)
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @"(\.\d{2})\d+", "$1");
            // 3. Remove excess whitespace/newlines between tags
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @">\s+<", "><");

            var preservedElements = new StringBuilder();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool foundCoords = false;

            int svgStart = finalSvg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (svgStart < 0) {
                log("ERROR: <svg tag found via Contains but IndexOf failed (Invalid SVG).");
                return Results.Problem("Invalid SVG");
            }
            
            int bodyStart = finalSvg.IndexOf(">", svgStart) + 1;
            int bodyEnd = finalSvg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < bodyStart) bodyEnd = finalSvg.Length;

            string header = finalSvg.Substring(svgStart, bodyStart - svgStart);
            bool hasExistingViewBox = System.Text.RegularExpressions.Regex.IsMatch(header, @"viewBox=""[^""]+""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            log($"SVG parsed. Header length: {header.Length}, Body length: {bodyEnd - bodyStart}, hasExistingViewBox: {hasExistingViewBox}. Scanning tags...");
            // --- 0. PRE-SCAN FOR HYPERLINKS WITH COORDINATES ---
            var hyperlinksData = new List<object>();
            var seenLinks = new HashSet<string>();
            var fullLinkPattern = @"<a\s+[^>]*?xlink:href=[""']([^""']+)[""'][^>]*?>(.*?)</a>";
            var linkMatches = System.Text.RegularExpressions.Regex.Matches(finalSvg, fullLinkPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            
            foreach (System.Text.RegularExpressions.Match lm in linkMatches) {
                string val = lm.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(val) || val.StartsWith("#") || val.StartsWith("data:")) continue;
                if (!seenLinks.Add(val)) continue;

                string inner = lm.Groups[2].Value;
                var coordMatch = System.Text.RegularExpressions.Regex.Match(inner, @"d=[""']M\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                double lx = 0, ly = 0;
                if (coordMatch.Success) {
                    double.TryParse(coordMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lx);
                    double.TryParse(coordMatch.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out ly);
                }
                hyperlinksData.Add(new { text = val, x = lx, y = ly });
            }
            log($"[HYPER] Raw SVG içinde {hyperlinksData.Count} benzersiz link ve koordinat bulundu.");

            // Global Path Aggregator (Reduces 140k nodes to < 100)
            var globalPathDataMap = new Dictionary<string, StringBuilder>();
            var globalSegmentsSeen = new HashSet<long>();

            // Fast Tag-by-Tag Scan
            int current = bodyStart;
            bool inDefs = false;
            int tagCount = 0;
            int mergedTagCount = 0;
            int dedupedTagCount = 0;
            int skippedTinyCount = 0;

            while (current < bodyEnd) {
                int nextTagStart = finalSvg.IndexOf('<', current);
                if (nextTagStart > current) {
                    string gap = finalSvg.Substring(current, nextTagStart - current);
                    if (!string.IsNullOrWhiteSpace(gap)) preservedElements.Append(gap);
                }
                
                if (nextTagStart < 0 || nextTagStart >= bodyEnd) break;
                
                int nextTagEnd = -1;
                bool tQuo = false;
                for (int i = nextTagStart; i < bodyEnd; i++) {
                    if (finalSvg[i] == '"') tQuo = !tQuo;
                    if (finalSvg[i] == '>' && !tQuo) { nextTagEnd = i; break; }
                }
                if (nextTagEnd < 0) break;

                string tagContent = finalSvg.Substring(nextTagStart, nextTagEnd - nextTagStart + 1);
                current = nextTagEnd + 1;

                string tagLower = tagContent.ToLower();
                if (tagLower.StartsWith("<defs") || inDefs) {
                    if (tagLower.StartsWith("<defs")) inDefs = true;
                    if (tagLower.Contains("</defs>")) inDefs = false;
                    preservedElements.Append(tagContent); continue;
                }

                if (tagLower.StartsWith("</") || tagLower.StartsWith("<text") || tagLower.StartsWith("<a")) {
                    preservedElements.Append(tagContent); continue;
                }

                // -- Color Transform --
                var colorMatch = System.Text.RegularExpressions.Regex.Match(tagContent, @"(?:stroke|fill)[:=]\s*""?\s*(#[0-9a-fA-F]{3,6}|rgb\([^)]+\)|[a-zA-Z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (colorMatch.Success) {
                    string c = colorMatch.Value.Split(new[] {':','='})[1].Trim(' ','"','\'').ToLower();
                    if (c != "none") {
                        if (IsTooDark(c)) tagContent = tagContent.Replace(colorMatch.Value, colorMatch.Value.Replace(c, "#cccccc"));
                        if (!tagContent.Contains("data-original-")) {
                            string attrType = colorMatch.Value.Contains("stroke") ? "stroke" : "fill";
                            int firstSpace = tagContent.IndexOf(' ');
                            if (firstSpace > 0) tagContent = tagContent.Insert(firstSpace, $" data-original-{attrType}=\"{c}\" class=\"color-group\"");
                        }
                    }
                }

                // -- Radical Merging --
                bool isMergeable = (tagLower.StartsWith("<path") || tagLower.StartsWith("<line") || tagLower.StartsWith("<polyline") || tagLower.StartsWith("<polygon"));
                if (isMergeable) {
                    tagCount++;
                    string? attrs = getPathAttrs(tagContent);
                    if (attrs == null) { dedupedTagCount++; continue; } 

                    string data = getPathData(tagContent);
                    if (string.IsNullOrEmpty(data)) continue;

                    if (!globalPathDataMap.ContainsKey(attrs)) globalPathDataMap[attrs] = new StringBuilder();
                    var targetSb = globalPathDataMap[attrs];

                    // -- SEGMENT FILTERING & DECIMATION --
                    var segMatches = System.Text.RegularExpressions.Regex.Matches(data, @"M\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)\s*L\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (segMatches.Count > 0) {
                        foreach (System.Text.RegularExpressions.Match sm in segMatches) {
                            double x1 = double.Parse(sm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                            double y1 = double.Parse(sm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                            double x2 = double.Parse(sm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                            double y2 = double.Parse(sm.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                            
                            // 1. Skip tiny segments (< 0.2 units)
                            double dx = x2 - x1; double dy = y2 - y1;
                            if (dx*dx + dy*dy < 0.04) { skippedTinyCount++; continue; }

                            // 2. Spatial Deduplication
                            long qx1 = (long)(Math.Round(x1 * 5)), qy1 = (long)(Math.Round(y1 * 5)), qx2 = (long)(Math.Round(x2 * 5)), qy2 = (long)(Math.Round(y2 * 5));
                            long h1 = qx1 ^ (qy1 << 16) ^ (qx2 << 32) ^ (qy2 << 48), h2 = qx2 ^ (qy2 << 16) ^ (qx1 << 32) ^ (qy1 << 48);
                            long segmentHash = Math.Min(h1, h2);

                            if (globalSegmentsSeen.Add(segmentHash)) {
                                if (targetSb.Length > 0) targetSb.Append(" ");
                                targetSb.Append(sm.Value);
                                mergedTagCount++;
                            } else dedupedTagCount++;
                        }
                    } else {
                        if (targetSb.Length > 0) targetSb.Append(" ");
                        targetSb.Append(data);
                        mergedTagCount++;
                    }
                } else {
                    preservedElements.Append(tagContent);
                }
            }

            // -- FLUSH GLOBAL PATHS --
            foreach (var kvp in globalPathDataMap) {
                if (kvp.Value.Length > 0) {
                    preservedElements.Append($"<path {kvp.Key} d=\"{kvp.Value.ToString()}\"/>");
                }
            }

            log($"[OPTI] Radical: {tagCount} tags -> {globalPathDataMap.Count} paths. Skipped: {skippedTinyCount} tiny, {dedupedTagCount} dupes.");

            // --- 3. RECONSTRUCT ---
            var finalBody = new StringBuilder();
            finalBody.AppendLine("<g id=\"main-content\">");
            finalBody.Append(preservedElements.ToString());
            finalBody.AppendLine("</g>");

            if (!hasExistingViewBox && foundCoords && minX != double.MaxValue) {
                double w = maxX - minX; double h = maxY - minY;
                double padding = Math.Max(w, h) * 0.05;
                string nv = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2}", minX - padding, minY - padding, w + 2 * padding, h + 2 * padding);
                header = System.Text.RegularExpressions.Regex.Replace(header, @"viewBox=""[^""]*""", $"viewBox=\"{nv}\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                log($"Calculated new ViewBox: {nv}");
            } else if (hasExistingViewBox) {
                log("Reusing existing ViewBox from LibreDWG output.");
            } else {
                log("WARNING: No valid coordinates found for ViewBox calculation. Kept original.");
            }
            header = System.Text.RegularExpressions.Regex.Replace(header, @"\swidth=""[^""]*""", " width=\"100%\"");
            header = System.Text.RegularExpressions.Regex.Replace(header, @"\sheight=""[^""]*""", " height=\"100%\"");

            // HARDWARE ACCELERATION CSS + DYNAMIC STYLE CLASSES
            string baseStyle = $@"<style>
                svg {{ overflow: hidden !important; }} 
                path, polyline, line, circle, rect, ellipse, polygon {{ vector-effect: none !important; pointer-events: none; stroke-width: 1px !important; }} 
                text {{ fill: #ffffff !important; font-family: 'Segoe UI', Arial, sans-serif; font-weight: bold; pointer-events: none; }}
                {cssRules.ToString()}
            </style>";
            
            finalSvg = header + "<defs>" + baseStyle + "</defs>" + finalBody.ToString() + "</svg>";

            await File.WriteAllTextAsync(outputPath, finalSvg);
            swOpt.Stop();
            long finalSize = new FileInfo(outputPath).Length;
            log($"SUCCESS: Optimization complete in {swOpt.ElapsedMilliseconds}ms. Final size: {finalSize} bytes (Original: {originalSize}).");
            
            var resultObj = new { 
                success = true, 
                svgUrl = $"/output/{outputFileName}",
                hyperlinks = hyperlinksData
            };
            
            // Auto-load için son yüklenen dosyayı JSON olarak kaydet
            await File.WriteAllTextAsync(Path.Combine(outputFolder, "latest_map.json"), System.Text.Json.JsonSerializer.Serialize(resultObj));

            return Results.Ok(resultObj);
        }
        else {
            log($"ERROR: SVG not found and stdout was empty or invalid. First 50 chars: {(output.Length > 50 ? output.Substring(0,50) : output)}... STDERR: {error}");
            return Results.Problem($"Conversion failed: {error}");
        }
    }
    catch (Exception ex) {
        log($"EXCEPTION CAUGHT: {ex.Message}\nStack Trace: {ex.StackTrace}");
        return Results.Problem($"Exec error: {ex.Message}");
    }
    } finally {
        System.Threading.Interlocked.Exchange(ref processingCount, 0);
    }
}).DisableAntiforgery();

app.MapGet("/logs", async () => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    if (File.Exists(logPath)) return Results.Text(await File.ReadAllTextAsync(logPath));
    return Results.NotFound("No logs available yet. Upload a DWG first.");
});

app.MapGet("/clear-logs", () => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    if (File.Exists(logPath)) File.Delete(logPath);
    return Results.Ok("Logs cleared.");
});

app.MapGet("/latest-map", async () => {
    var latestJson = Path.Combine(Directory.GetCurrentDirectory(), "output", "latest_map.json");
    if (File.Exists(latestJson)) {
        return Results.Text(await File.ReadAllTextAsync(latestJson), "application/json");
    }
    return Results.NotFound(new { success = false, message = "No recent map found." });
});

// Serve the output folder
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "output")),
    RequestPath = "/output"
});

app.Run();

